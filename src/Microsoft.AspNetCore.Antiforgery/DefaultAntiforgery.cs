// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Antiforgery
{
    /// <summary>
    /// Provides access to the antiforgery system, which provides protection against
    /// Cross-site Request Forgery (XSRF, also called CSRF) attacks.
    /// </summary>
    public class DefaultAntiforgery : IAntiforgery
    {
        private readonly AntiforgeryOptions _options;
        private readonly IAntiforgeryTokenGenerator _tokenGenerator;
        private readonly IAntiforgeryTokenSerializer _tokenSerializer;
        private readonly IAntiforgeryTokenStore _tokenStore;

        public DefaultAntiforgery(
            IOptions<AntiforgeryOptions> antiforgeryOptionsAccessor,
            IAntiforgeryTokenGenerator tokenGenerator,
            IAntiforgeryTokenSerializer tokenSerializer,
            IAntiforgeryTokenStore tokenStore)
        {
            _options = antiforgeryOptionsAccessor.Value;
            _tokenGenerator = tokenGenerator;
            _tokenSerializer = tokenSerializer;
            _tokenStore = tokenStore;
        }

        /// <inheritdoc />
        public IHtmlContent GetHtml(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CheckSSLConfig(context);

            var tokenSet = GetAndStoreTokens(context);
            return new InputContent(_options.FormFieldName, tokenSet.RequestToken);
        }

        /// <inheritdoc />
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CheckSSLConfig(context);

            var tokenSet = GetTokensInternal(context);
            if (tokenSet.IsNewCookieToken)
            {
                SaveCookieTokenAndHeader(context, tokenSet.CookieToken);
            }

            return Serialize(tokenSet);
        }

        /// <inheritdoc />
        public AntiforgeryTokenSet GetTokens(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CheckSSLConfig(context);

            var tokenSet = GetTokensInternal(context);
            return Serialize(tokenSet);
        }

        /// <inheritdoc />
        public async Task ValidateRequestAsync(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CheckSSLConfig(context);

            var tokens = await _tokenStore.GetRequestTokensAsync(context);
            ValidateTokens(context, tokens);
        }

        /// <inheritdoc />
        public void ValidateTokens(HttpContext context, AntiforgeryTokenSet antiforgeryTokenSet)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CheckSSLConfig(context);

            if (string.IsNullOrEmpty(antiforgeryTokenSet.CookieToken))
            {
                throw new ArgumentException(
                    Resources.Antiforgery_CookieToken_MustBeProvided_Generic,
                    nameof(antiforgeryTokenSet));
            }

            if (string.IsNullOrEmpty(antiforgeryTokenSet.RequestToken))
            {
                throw new ArgumentException(
                    Resources.Antiforgery_RequestToken_MustBeProvided_Generic,
                    nameof(antiforgeryTokenSet));
            }

            // Extract cookie & request tokens
            var deserializedCookieToken = _tokenSerializer.Deserialize(antiforgeryTokenSet.CookieToken);
            var deserializedRequestToken = _tokenSerializer.Deserialize(antiforgeryTokenSet.RequestToken);

            // Validate
            _tokenGenerator.ValidateTokens(
                context,
                deserializedCookieToken,
                deserializedRequestToken);
        }

        /// <inheritdoc />
        public void SetCookieTokenAndHeader(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CheckSSLConfig(context);

            var cookieToken = GetCookieTokenDoesNotThrow(context);
            cookieToken = ValidateAndGenerateNewCookieToken(cookieToken);
            SaveCookieTokenAndHeader(context, cookieToken);
        }

        // This method returns null if oldCookieToken is valid.
        private AntiforgeryToken ValidateAndGenerateNewCookieToken(AntiforgeryToken cookieToken)
        {
            if (!_tokenGenerator.IsCookieTokenValid(cookieToken))
            {
                // Need to make sure we're always operating with a good cookie token.
                var newCookieToken = _tokenGenerator.GenerateCookieToken();
                Debug.Assert(_tokenGenerator.IsCookieTokenValid(newCookieToken));
                return newCookieToken;
            }

            return null;
        }

        private void SaveCookieTokenAndHeader(
            HttpContext context,
            AntiforgeryToken cookieToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (cookieToken != null)
            {
                // Persist the new cookie if it is not null.
                _tokenStore.SaveCookieToken(context, cookieToken);
            }

            if (!_options.SuppressXFrameOptionsHeader)
            {
                // Adding X-Frame-Options header to prevent ClickJacking. See
                // http://tools.ietf.org/html/draft-ietf-websec-x-frame-options-10
                // for more information.
                context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            }
        }

        private void CheckSSLConfig(HttpContext context)
        {
            if (_options.RequireSsl && !context.Request.IsHttps)
            {
                throw new InvalidOperationException(Resources.FormatAntiforgeryWorker_RequireSSL(
                    nameof(AntiforgeryOptions),
                    nameof(AntiforgeryOptions.RequireSsl),
                    "true"));
            }
        }

        private AntiforgeryToken GetCookieTokenDoesNotThrow(HttpContext context)
        {
            try
            {
                return _tokenStore.GetCookieToken(context);
            }
            catch
            {
                // ignore failures since we'll just generate a new token
                return null;
            }
        }

        private AntiforgeryTokenSetInternal GetTokensInternal(HttpContext context)
        {
            var cookieToken = GetCookieTokenDoesNotThrow(context);
            var newCookieToken = ValidateAndGenerateNewCookieToken(cookieToken);
            if (newCookieToken != null)
            {
                cookieToken = newCookieToken;
            }
            var requestToken = _tokenGenerator.GenerateRequestToken(
                context,
                cookieToken);

            return new AntiforgeryTokenSetInternal()
            {
                // Note : The new cookie would be null if the old cookie is valid.
                CookieToken = cookieToken,
                RequestToken = requestToken,
                IsNewCookieToken = newCookieToken != null
            };
        }

        private AntiforgeryTokenSet Serialize(AntiforgeryTokenSetInternal tokenSet)
        {
            return new AntiforgeryTokenSet(
                tokenSet.RequestToken != null ? _tokenSerializer.Serialize(tokenSet.RequestToken) : null,
                tokenSet.CookieToken != null ? _tokenSerializer.Serialize(tokenSet.CookieToken) : null);
        }

        private class AntiforgeryTokenSetInternal
        {
            public AntiforgeryToken RequestToken { get; set; }

            public AntiforgeryToken CookieToken { get; set; }

            public bool IsNewCookieToken { get; set; }
        }

        private class InputContent : IHtmlContent
        {
            private readonly string _fieldName;
            private readonly string _requestToken;

            public InputContent(string fieldName, string requestToken)
            {
                _fieldName = fieldName;
                _requestToken = requestToken;
            }

            // Though _requestToken normally contains only US-ASCII letters, numbers, '-', and '_', must assume the
            // IAntiforgeryTokenSerializer implementation has been overridden. Similarly, users may choose a
            // _fieldName containing almost any character.
            public void WriteTo(TextWriter writer, HtmlEncoder encoder)
            {
                var builder = writer as IHtmlContentBuilder;
                if (builder != null)
                {
                    // If possible, defer encoding until we're writing to the response.
                    // But there's little reason to keep this IHtmlContent instance around.
                    builder
                        .AppendHtml("<input name=\"")
                        .Append(_fieldName)
                        .AppendHtml("\" type=\"hidden\" value=\"")
                        .Append(_requestToken)
                        .AppendHtml("\" />");
                }

                writer.Write("<input name=\"");
                encoder.Encode(writer, _fieldName);
                writer.Write("\" type=\"hidden\" value=\"");
                encoder.Encode(writer, _requestToken);
                writer.Write("\" />");
            }
        }
    }
}