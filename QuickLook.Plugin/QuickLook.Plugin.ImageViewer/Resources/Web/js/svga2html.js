/**
 * SvgaViewer: Provides SVGA animation preview with the following features.
 * 
 * Requirements:
 * - Requires the following HTML structure:
 *   <canvas id="canvas">
 *   </canvas>
 * - SVGA file path is obtained via chrome.webview.hostObjects.external.GetPath()
 * 
 * Features:
 * - Loads and plays SVGA animation files
 * - Uses svga or svgaplayerweb library for parsing and playback
 * - Automatically starts playback after loading
 * - Handles asynchronous loading and mounting of SVGA files 
*/
class SvgaViewer {
    constructor() {
        this.useLite = false; // Use SVGA Lite version if true
    }

    /**
     * Play SVGA file.
     * @async
     */
    async play() {
        try {
            const path = await chrome.webview.hostObjects.external.GetPath();

            if (this.useLite) {
                const parser = new SVGA.Parser(); // Only SVGA 2.x supported

                // Because the path is a local file, we need to convert it to a URL format
                parser.load('https://' + path).then(svga => {
                    const player = new SVGA.Player(document.getElementById('canvas'));
                    player.mount(svga).then(() => {
                        player.start();
                        SvgaViewer.markReady();
                    });
                }).catch(() => SvgaViewer.markFailed());
            } else {
                const size = JSON.parse(await chrome.webview.hostObjects.external.GetSize());
                const parser = new SVGA.Parser('#canvas'); // Only SVGA 2.x supported
                const player = new SVGA.Player('#canvas');
                const canvas = document.getElementById('canvas');

                canvas.width = size.width;
                canvas.height = size.height;

                // Because the path is a local file, we need to convert it to a URL format
                parser.load('https://' + path, function (videoItem) {
                    if (!videoItem) {
                        SvgaViewer.markFailed();
                        return;
                    }

                    player.setVideoItem(videoItem);
                    player.startAnimation();
                    SvgaViewer.markReady();
                });
            }
        } catch (error) {
            SvgaViewer.markFailed();
        }
    }

    static markReady() {
        const canvas = document.getElementById('canvas');
        if (canvas)
            canvas.dataset.quicklookReady = '1';
        window.__quicklookPostReady?.();
    }

    static markFailed() {
        if (!document.getElementById('__quicklook_svga_error')) {
            const message = document.createElement('div');
            message.id = '__quicklook_svga_error';
            message.textContent = 'This animation could not be rendered.';
            message.style.cssText =
                'position:fixed;inset:0;display:flex;align-items:center;justify-content:center;' +
                'padding:32px;font:14px/1.6 "Segoe UI",sans-serif;color:#888;text-align:center;';
            document.body.appendChild(message);
        }

        window.__quicklookPostReady?.();
    }
}

// Create the SVGA viewer and play
new SvgaViewer().play().catch(() => SvgaViewer.markFailed());
