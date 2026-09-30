// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Online
{
    /// <summary>
    /// Pure URL trust checks against the configured type!beat endpoint hosts, shared by
    /// <see cref="TrustedDomainOnlineStore"/> (online resource lookups) and
    /// <see cref="Chat.ExternalLinkOpener"/> (external link warning suppression).
    /// A URL is trusted when it is an absolute http(s) URL whose host is loopback (local development),
    /// an exact match of a configured endpoint host, a subdomain of a configured endpoint's apex domain,
    /// or (when production is the configured endpoint) the host production used to live on.
    /// </summary>
    public static class TrustedDomains
    {
        /// <summary>
        /// Checks <paramref name="url"/> against the <see cref="EndpointConfiguration.APIUrl"/>
        /// and <see cref="EndpointConfiguration.WebsiteUrl"/> hosts of <paramref name="endpoints"/>.
        /// </summary>
        public static bool IsTrustedUrl(string url, EndpointConfiguration endpoints)
        {
            Uri.TryCreate(endpoints.APIUrl, UriKind.Absolute, out Uri? apiUri);
            Uri.TryCreate(endpoints.WebsiteUrl, UriKind.Absolute, out Uri? websiteUri);

            return IsTrustedUrl(url, apiUri, websiteUri);
        }

        /// <summary>
        /// Checks <paramref name="url"/> against pre-parsed endpoint URIs.
        /// </summary>
        public static bool IsTrustedUrl(string url, Uri? apiUri, Uri? websiteUri)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return false;

            // Host-based trust only makes sense for web URLs; anything else
            // (mailto:, custom schemes, ...) is never treated as a trusted domain.
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            if (uri.IsLoopback)
                return true;

            return hostMatches(uri, apiUri) || hostMatches(uri, websiteUri) || isLegacyProductionHost(uri, apiUri, websiteUri);
        }

        // Links to the pre-move production host stay trusted, but only while production is what
        // this client is actually configured against: a dev or override target has no business
        // trusting a production host. The host and its subdomains (the direct-origin bss host) only,
        // deliberately not the old apex, which was never type!beat's own domain.
        private static bool isLegacyProductionHost(Uri candidate, Uri? apiUri, Uri? websiteUri)
        {
            string productionHost = new Uri(TypebeatEndpointConfiguration.PRODUCTION_ROOT).Host;

            bool onProduction = string.Equals(apiUri?.Host, productionHost, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(websiteUri?.Host, productionHost, StringComparison.OrdinalIgnoreCase);

            if (!onProduction)
                return false;

            const string legacy = TypebeatEndpointConfiguration.LEGACY_PRODUCTION_HOST;

            return candidate.Host.Equals(legacy, StringComparison.OrdinalIgnoreCase)
                   || candidate.Host.EndsWith($@".{legacy}", StringComparison.OrdinalIgnoreCase);
        }

        // Trust the endpoint host itself and any subdomain of its apex (the maps/media host will live
        // on a sibling subdomain of the same apex once file serving lands).
        private static bool hostMatches(Uri candidate, Uri? trusted)
        {
            if (trusted == null)
                return false;

            if (candidate.Host.Equals(trusted.Host, StringComparison.OrdinalIgnoreCase))
                return true;

            string apex = apexOf(trusted.Host);
            return candidate.Host.EndsWith($@".{apex}", StringComparison.OrdinalIgnoreCase);
        }

        private static string apexOf(string host)
        {
            string[] parts = host.Split('.');
            return parts.Length <= 2 ? host : string.Join(@".", parts[^2..]);
        }
    }
}
