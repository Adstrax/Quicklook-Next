/**
 * LottieViewer: Provides Lottie animation preview with the following features.
 * 
 * Requirements:
 * - Requires the following HTML structure:
 *   <div id="bm"></div>
 * - Lottie file path is obtained via chrome.webview.hostObjects.external.GetPath()
 * 
 * Features:
 * - Loads and plays Lottie animation files
 * - Uses lottie-web library for parsing and playback
 * - Automatically starts playback after loading
 * - Handles asynchronous loading and mounting of Lottie files
 */
class LottieViewer {
    constructor() {
    }

    /**
     * Play Lottie files.
     * @async
     */
    async play() {
        try {
            const path = await chrome.webview.hostObjects.external.GetPath();

            // Because the path is a local file, we need to convert it to a URL format
            const animation = lottie.loadAnimation({
                container: document.getElementById('bm'),
                renderer: 'svg',
                loop: true,
                autoplay: true,
                path: 'https://' + path,
            });
            animation.addEventListener('DOMLoaded', () => window.__quicklookPostReady?.());
            // A malformed animation never fires DOMLoaded; report it instead
            // of leaving the preview waiting for the host's fallback.
            animation.addEventListener('data_failed', () => LottieViewer.showFailure());
            animation.addEventListener('error', () => LottieViewer.showFailure());
        } catch (error) {
            LottieViewer.showFailure();
        }
    }

    static showFailure() {
        const container = document.getElementById('bm');
        if (container && !container.querySelector('.quicklook-lottie-error')) {
            const message = document.createElement('div');
            message.className = 'quicklook-lottie-error';
            message.textContent = 'This animation could not be rendered.';
            message.style.cssText =
                'position:absolute;inset:0;display:flex;align-items:center;justify-content:center;' +
                'padding:32px;font:14px/1.6 "Segoe UI",sans-serif;color:#888;text-align:center;';
            container.appendChild(message);
        }

        window.__quicklookPostReady?.();
    }
}

// Create the Lottie viewer and play
new LottieViewer().play().catch(() => LottieViewer.showFailure());
