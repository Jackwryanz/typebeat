// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Online;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the endpoint-host trust logic shared by <see cref="Game.Online.Chat.ExternalLinkOpener"/>
    /// (external link warning suppression) and <see cref="TrustedDomainOnlineStore"/> (online resource lookups).
    /// Notably pins that trust is decided on the parsed host, not a raw string prefix:
    /// "https://typebeat.sh.evil.com" must not pass as trusted.
    /// </summary>
    [TestFixture]
    public class TrustedDomainsTest
    {
        [TestCase("https://typebeat.sh")]
        [TestCase("https://typebeat.sh/beatmapsets/123")]
        [TestCase("https://TYPEBEAT.SH/beatmapsets/123")]
        [TestCase("https://maps.typebeat.sh/previews/123.mp3")] // subdomain of the apex (future media host)
        [TestCase("https://bss.typebeat.sh/releases")]
        [TestCase("https://typebeat.mingda.sh/beatmapsets/123")] // the pre-move host: old pasted links stay trusted
        [TestCase("https://TYPEBEAT.MINGDA.SH/beatmapsets/123")]
        [TestCase("https://bss.typebeat.mingda.sh/releases")]
        [TestCase("http://localhost:5089/beatmapsets/123")] // loopback local development
        [TestCase("http://127.0.0.1:5089/previews/123.mp3")]
        public void TrustedUrls(string url)
            => Assert.That(TrustedDomains.IsTrustedUrl(url, new TypebeatEndpointConfiguration()), Is.True);

        [TestCase("https://typebeat.sh.evil.com/phish")] // the raw prefix bypass this logic replaces
        [TestCase("https://typebeat.sh@evil.com/phish")] // userinfo trick: actual host is evil.com
        [TestCase("https://evil.com/https://typebeat.sh")]
        [TestCase("https://eviltypebeat.sh/x")] // apex must match on a label boundary
        [TestCase("https://evil.sh/x")] // the TLD is not an apex: a two-label endpoint trusts only itself and its subdomains
        [TestCase("https://typebeat.mingda.sh.evil.com/phish")] // the same bypasses against the pre-move host
        [TestCase("https://eviltypebeat.mingda.sh/x")]
        [TestCase("https://mingda.sh")] // the pre-move apex was never type!beat's own domain
        [TestCase("https://maps.mingda.sh/previews/123.mp3")] // nor were its other subdomains
        [TestCase("https://example.com")]
        [TestCase("mailto:someone@typebeat.sh")] // non-web schemes are never a trusted domain
        [TestCase("typebeat://s/123")]
        [TestCase("/beatmapsets/123")] // relative: callers resolve against the website root before checking
        [TestCase("not a url")]
        public void UntrustedUrls(string url)
            => Assert.That(TrustedDomains.IsTrustedUrl(url, new TypebeatEndpointConfiguration()), Is.False);

        [Test]
        public void LoopbackDevEndpointsRemainTrusted()
        {
            var devEndpoints = new TypebeatEndpointConfiguration("http://localhost:5089");

            Assert.That(TrustedDomains.IsTrustedUrl("http://localhost:5089/beatmapsets/123", devEndpoints), Is.True);
            Assert.That(TrustedDomains.IsTrustedUrl("https://evil.com/", devEndpoints), Is.False);
            // the pre-move production host is only trusted by a client configured against production.
            Assert.That(TrustedDomains.IsTrustedUrl("https://typebeat.mingda.sh/beatmapsets/123", devEndpoints), Is.False);
        }

        /// <summary>
        /// Production beatmap submission is pinned to the direct-origin host that bypasses
        /// Cloudflare (see <see cref="TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>),
        /// not the CF-proxied website/API root.
        /// </summary>
        [Test]
        public void ProductionBeatmapSubmissionUsesDirectOriginHost()
        {
            var endpoints = new TypebeatEndpointConfiguration();

            Assert.That(endpoints.BeatmapSubmissionServiceUrl, Is.EqualTo("https://bss.typebeat.sh/bss"));
        }

        /// <summary>
        /// Regression guard for the dev / TYPEBEAT_API_URL-override path: a non-production
        /// apiRoot must keep deriving BeatmapSubmissionServiceUrl from itself rather than
        /// silently targeting production's direct-origin BSS host.
        /// </summary>
        [Test]
        public void NonProductionApiRootDerivesOwnBeatmapSubmissionUrl()
        {
            var endpoints = new TypebeatEndpointConfiguration("http://localhost:5089");

            Assert.That(endpoints.BeatmapSubmissionServiceUrl, Is.EqualTo("http://localhost:5089/bss"));
        }
    }
}
