﻿// <copyright file="SchwabTokens.cs" company="ZPM Software Inc">
// Copyright © 2024 ZPM Software Inc. All rights reserved.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. http://mozilla.org/MPL/2.0/.
// </copyright>

using System;
using System.IO;
using System.Text;
using System.Windows;
using Newtonsoft.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Net.Http;
using static SchwabApiCS.SchwabApi;

namespace SchwabApiCS
{
    public class SchwabTokens
    {
        private const string baseUrl = "https://api.schwabapi.com/v1/oauth";
        private SchwabTokensData tokens;
        private string tokenDataFileName;
        private WebView2 webView = null;  // webbrowser object used to get initial AccessToken, or when RefreshToken expires

        /// <summary>
        /// Loads saved tokens info
        /// </summary>
        /// <param name="webView">WebView2 control that will get authorization code</param>
        public SchwabTokens(string _tokenDataFileName, WebView2 webView)
        {
            this.webView = webView;
            this.tokenDataFileName = _tokenDataFileName;

            using (StreamReader sr = new StreamReader(tokenDataFileName))  // load saved tokens
            {
                var jsonTokens = sr.ReadToEnd();
                tokens = JsonConvert.DeserializeObject<SchwabTokensData>(jsonTokens);

                if (tokens.AccessToken == "") // first time use, or to reset the tokens, set AccessToken to ""
                { // this will cause reauthorization
                    tokens.AccessTokenExpires = DateTime.Now.AddDays(-1);
                    tokens.RefreshTokenExpires = DateTime.Now.AddDays(-1);
                }
            }
            if (string.IsNullOrEmpty(tokens.AppKey)) throw new SchwabApiException("Schwab AppKey is not defined");
            if (string.IsNullOrEmpty(tokens.Secret)) throw new SchwabApiException("Schwab Secret is not defined");
            if (string.IsNullOrEmpty(tokens.Redirect_uri)) throw new SchwabApiException("SchwabRedirect_uri is not defined");
             
        }

        public bool NeedsReAuthorization {  get { return DateTime.Now >= tokens.RefreshTokenExpires; } }

        private System.Uri AuthorizeUri
        {
            get { return new System.Uri(baseUrl + "/authorize?client_id=" + tokens.AppKey + "&redirect_uri="+ tokens.Redirect_uri); }
        }

        /// <summary>
        /// Get Access Token, checks for expiration & renews.
        /// </summary>
        public string AccessToken
        {
            get
            {
                if (tokens == null || DateTime.Now >= tokens.AccessTokenExpires) {
                    try
                    {
                        var task = GetAccessToken();
                        task.Wait();
                    }
                    catch (Exception ex)
                    {
                        throw;
                    }
                }
                return tokens.AccessToken;
            }
        }

        /// <summary>
        /// Get Access Token, checks for expiration & renews. Should be called for every new process.
        /// </summary>
        /// <returns></returns>
        private async Task<string> GetAccessToken()
        {
            try
            {
                if (DateTime.Now < tokens.AccessTokenExpires)
                    return tokens.AccessToken;

                if (DateTime.Now >= tokens.RefreshTokenExpires) // need to re-authorize
                    throw new SchwabApiException("GetAccessToken: reautrhorization required");

                var httpClient = new HttpClient();
                var content = new StringContent("grant_type=refresh_token&refresh_token=" + tokens.RefreshToken, Encoding.UTF8, "application/x-www-form-urlencoded");
                httpClient.DefaultRequestHeaders.Add("Authorization", "Basic " + SchwabApi.Base64Encode(tokens.AppKey + ":" + tokens.Secret));
                var response = httpClient.PostAsync(baseUrl + "/token", content).Result;
                //var responseJson = response.Content.ReadAsStringAsync();
                //responseJson.Wait();
                SaveTokens(response, "GetAccessToken");
                return tokens.AccessToken;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public delegate void AppStartCallback();
        private AppStartCallback appStartCallback;

        public void ReAuthorize(WebView2 webView, AppStartCallback appStartCallback)
        {
            this.appStartCallback = appStartCallback;

            webView.Dispatcher.Invoke(() =>
            {
                webView.Visibility = Visibility.Visible;
                webView.NavigationCompleted += NavCompleted;
                webView.Source = AuthorizeUri;
            });

            tokens.RefreshToken = "";
        }

        /// <summary>
        /// Website re-authorization complete 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void NavCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            var wv = (Microsoft.Web.WebView2.Wpf.WebView2)sender;

            if (wv.Source.AbsoluteUri.StartsWith("https://127.0.0.1"))
            {

                if (wv.Source.AbsoluteUri.Contains("error=access_denied"))
                    throw new SchwabApiException("Reauthorization was not completed properly, restart and try again.");

                var p = wv.Source.AbsoluteUri.IndexOf("code=") + 5;
                var p2 = wv.Source.AbsoluteUri.IndexOf("&session");
                var authCode = Uri.UnescapeDataString(wv.Source.AbsoluteUri.Substring(p, p2 - p));

                var httpClient = new HttpClient();
                var content = new StringContent("code=" + authCode + "&redirect_uri=" + tokens.Redirect_uri + "&grant_type=authorization_code",
                                                Encoding.UTF8, "application/x-www-form-urlencoded");

                httpClient.DefaultRequestHeaders.Add("Authorization", "Basic " + SchwabApi.Base64Encode(tokens.AppKey + ":" + tokens.Secret));

                var response = httpClient.PostAsync(baseUrl + "/token", content).Result;
                SaveTokens(response, "NavCompleted");

                webView.Visibility = Visibility.Collapsed;
                webView.NavigationCompleted -= NavCompleted;
                appStartCallback();
            }
        }

        /// <summary>
        /// extract tokens and save
        /// </summary>
        /// <param name="responseJson"></param>
        /// <param name="callingMethod">name of calling method </param>
        /// <exception cref="Exception"></exception>
        private void SaveTokens(HttpResponseMessage response, string callingMethod)
        {
            var responseJson = response.Content.ReadAsStringAsync();
            responseJson.Wait();
            var result = JsonConvert.DeserializeObject<dynamic>(responseJson.Result);

            if (result["error"] != null)
            {
                var msg = (string)result["error_description"].ToString();
                var p = msg.IndexOf("expiration=");
                if (p >= 0)
                {
                    var p2 = msg.IndexOf(",", p);
                    var expiration = SchwabApi.ApiDateTime_to_DateTime(Convert.ToInt64(msg.Substring(p + 11, p2 - p - 11)));
                    p = msg.IndexOf("now=");
                    p2 = msg.IndexOf(" ", p);
                    var now = SchwabApi.ApiDateTime_to_DateTime(Convert.ToInt64(msg.Substring(p + 4, p2 - p - 4)));

                    throw new SchwabApiAuthorizationException(response, 
                                callingMethod + ": Token Expired\nExpiration: " + expiration.ToString() + ", Now: " + now.ToString());
                }
                else
                {
                    throw new SchwabApiAuthorizationException(response, callingMethod + ": Token Authorization failed." +
                        ((int)response.StatusCode).ToString() + ": " + response.ReasonPhrase);
                }
            }
            else
            {
                // A Trader API access token is valid for 30 minutes. A Trader API refresh token is valid for 7 days.
                var r = JsonConvert.DeserializeObject<TokenResult>(responseJson.Result);
                this.tokens.AccessToken = r.access_token;
                this.tokens.RefreshToken = r.refresh_token;
                this.tokens.AccessTokenExpires = DateTime.Now.AddSeconds(r.expires_in - 10);
                this.tokens.RefreshTokenExpires = DateTime.Today.AddDays(7);
                SaveTokens();
            }
        }

        /// <summary>
        /// save to file
        /// </summary>
        public void SaveTokens()
        {
            using (StreamWriter sw = new StreamWriter(tokenDataFileName, false))  // save tokens
            {
                var jsonTokens = JsonConvert.SerializeObject(tokens, Formatting.Indented);
                sw.WriteLine(jsonTokens);
            }
        }

        /// <summary>
        /// result from token request
        /// </summary>
        private class TokenResult
        {
            public int expires_in { get; set; }
            public string token_type { get; set; }
            public string refresh_token { get; set; } // good for 7 days
            public string access_token { get; set; }  // good for 30 minutes
            public string scope { get; set; }
            public string id_token { get; set; }
        };

        /// <summary>
        /// What is saved in the token data file
        /// </summary>
        private class SchwabTokensData
        {
            public string AccessToken { get; set; } = "";
            public string RefreshToken { get; set; } = "";
            public DateTime AccessTokenExpires { get; set; }
            public DateTime RefreshTokenExpires { get; set; }

            public string AppKey { get; set; } = "";
            public string Secret { get; set; } = "";
            public string Redirect_uri { get; set; } = "";
        }
    }
}

