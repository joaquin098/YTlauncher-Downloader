using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;

namespace YTMusicLauncher.Services
{
    public class AdBlockerService
    {
        public async Task InitializeAdBlockerAsync(CoreWebView2 webView)
        {
            if (webView == null) return;

            webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            webView.WebResourceRequested += OnWebResourceRequested;

            string script = @"
(function() {
    'use strict';

    // 1. Neutralize pre-roll ads on ytInitialPlayerResponse
    let _ytPR;
    Object.defineProperty(window, 'ytInitialPlayerResponse', {
        get() { return _ytPR; },
        set(val) {
            if (val) {
                delete val.adPlacements;
                delete val.playerAds;
                delete val.adSlots;
                delete val.adBreakHeartbeatParams;
            }
            _ytPR = val;
        },
        configurable: true
    });

    // 2. Intercept fetch to strip ad nodes from API responses
    const _fetch = window.fetch;
    window.fetch = async function(...args) {
        const res = await _fetch.apply(this, args);
        try {
            const url = (typeof args[0] === 'string' ? args[0] : args[0]?.url) || '';
            if (url.includes('/youtubei/v1/player') || url.includes('/youtubei/v1/next')) {
                const json = await res.clone().json();
                delete json.adPlacements; delete json.playerAds;
                delete json.adSlots; delete json.adBreakHeartbeatParams;
                return new Response(JSON.stringify(json), {
                    status: res.status, statusText: res.statusText, headers: res.headers
                });
            }
        } catch(_) {}
        return res;
    };

    // 3. CSS – hide ad/promo/banner elements
    function injectCSS() {
        if (document.getElementById('__ublock_css')) return;
        const s = document.createElement('style');
        s.id = '__ublock_css';
        s.textContent = `
            ytmusic-ad-slot-renderer, ytmusic-banner-promo-renderer,
            ytmusic-mealbar-promo-renderer, ytmusic-you-there-renderer,
            .ytp-ad-module, .ytp-ad-overlay-container, .ytp-ad-text-inline,
            .ytp-ad-preview-text, #masthead-ad, .video-ads,
            tp-yt-paper-dialog:has(ytmusic-you-there-renderer) {
                display:none!important;
                visibility:hidden!important;
                height:0!important;
                pointer-events:none!important;
            }`;
        (document.head || document.documentElement).appendChild(s);
    }

    // 4. Global capturing event listener for ALL video play events & arrow keys
    document.addEventListener('play', function(e) {
        if (e.target && e.target.tagName === 'VIDEO') {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'ytm_user_play' }));
            }
        }
    }, true);

    document.addEventListener('keydown', function(e) {
        if (e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.isContentEditable)) return;
        if (e.key === 'ArrowLeft') {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'ytm_key_left' }));
            }
        } else if (e.key === 'ArrowRight') {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'ytm_key_right' }));
            }
        }
    }, true);

    // 5. Periodic status sync loop
    function tick() {
        injectCSS();

        // Skip-button clicking
        document.querySelectorAll(
            '.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.ytp-ad-overlay-close-button'
        ).forEach(b => { try { b.click(); } catch(_) {} });

        // Force-skip ad video
        const player = document.getElementById('movie_player');
        if (player) {
            const vid = player.querySelector('video');
            const adShowing = player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting');
            if (adShowing && vid) {
                vid.muted = true;
                vid.playbackRate = 16;
                if (vid.duration > 0) vid.currentTime = vid.duration - 0.1;
            }
        }

        // Dismiss 'Are you still watching?' dialogs
        document.querySelectorAll('ytmusic-you-there-renderer #button, tp-yt-paper-button#button')
            .forEach(b => { try { if (b.offsetParent) b.click(); } catch(_) {} });

        // Sync metadata & progress
        try {
            const vid = document.querySelector('video');
            if (!vid) return;

            const ms = navigator.mediaSession;
            const meta = ms && ms.metadata;

            const title  = meta?.title  || '';
            const artist = meta?.artist || '';
            let cover = '';
            if (meta?.artwork?.length) {
                const best = meta.artwork.reduce((a, b) => {
                    const aw = parseInt(a.sizes?.split('x')[0] || '0');
                    const bw = parseInt(b.sizes?.split('x')[0] || '0');
                    return bw > aw ? b : a;
                });
                cover = best.src || '';
            }

            const pageUrl = window.location.href;
            const currentTime = isNaN(vid.currentTime) ? 0 : vid.currentTime;
            const duration    = isNaN(vid.duration)    ? 0 : vid.duration;
            const isPlaying   = !vid.paused && !vid.ended && vid.readyState > 2 && duration > 0;

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({
                    type: 'ytm_state',
                    title,
                    artist,
                    cover,
                    url: pageUrl,
                    currentTime,
                    duration,
                    isPlaying
                }));
            }
        } catch(_) {}
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setInterval(tick, 350));
    } else {
        setInterval(tick, 350);
    }
})();
";
            await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (sender is not CoreWebView2 wv) return;
            var uri = e.Request.Uri.ToLowerInvariant();
            if (uri.Contains("doubleclick.net") ||
                uri.Contains("googleadservices.com") ||
                uri.Contains("googlesyndication.com") ||
                uri.Contains("/pagead/") ||
                uri.Contains("/ptracking") ||
                uri.Contains("/api/stats/ads") ||
                uri.Contains("adservice.google.com"))
            {
                e.Response = wv.Environment.CreateWebResourceResponse(null, 204, "No Content", "");
            }
        }
    }
}
