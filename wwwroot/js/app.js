// FFmpeg WebUI 前端辅助脚本
// 只提供浏览器能力封装，业务逻辑全部在 Blazor 组件中。
(function () {
    'use strict';

    const ffui = {};

    /* ── 主题 ──────────────────────────────────────────────────────────
       主题属性必须挂在 <html> 上，否则 body 的背景色无法跟随切换。 */
    ffui.applyTheme = function (theme) {
        const root = document.documentElement;
        const resolved = (theme === 'System' || !theme)
            ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'Dark' : 'Light')
            : theme;
        root.setAttribute('data-theme', resolved);
        root.setAttribute('data-theme-mode', theme || 'System');
        return resolved;
    };

    /* 记住主题，供服务端首屏渲染时直接使用（避免深色系统下闪白）。
       用独立函数而不是 eval：既更清晰，也不会被 CSP 的 unsafe-eval 限制挡住。 */
    ffui.setThemeCookie = function (theme) {
        try {
            const value = encodeURIComponent(theme || 'System');
            document.cookie = `ffui-theme=${value};path=/;max-age=31536000;SameSite=Lax`;
            return true;
        } catch (e) {
            return false;
        }
    };

    ffui.watchSystemTheme = function () {
        if (ffui._themeListener) return;
        const media = window.matchMedia('(prefers-color-scheme: dark)');
        ffui._themeListener = function () {
            if (document.documentElement.getAttribute('data-theme-mode') === 'System') {
                document.documentElement.setAttribute('data-theme', media.matches ? 'Dark' : 'Light');
            }
        };
        media.addEventListener('change', ffui._themeListener);
    };

    /* ── 剪贴板 ────────────────────────────────────────────────────────
       返回布尔值而不是抛异常，调用方据此给出「已复制」或「复制失败」提示。 */
    ffui.copyText = async function (text) {
        if (text === null || text === undefined) return false;
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch (e) {
            // 回退到 execCommand
        }
        try {
            const area = document.createElement('textarea');
            area.value = text;
            area.setAttribute('readonly', '');
            area.style.position = 'fixed';
            area.style.top = '-1000px';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            area.setSelectionRange(0, area.value.length);
            const ok = document.execCommand('copy');
            document.body.removeChild(area);
            return ok;
        } catch (e) {
            return false;
        }
    };

    /* ── 日志滚动 ──────────────────────────────────────────────────────
       autoStick 为 true 时贴底；用户手动上滚后不再强制拉回底部。 */
    ffui.initLogScroll = function (element, autoStick) {
        if (!element) return;
        element._autoStick = autoStick !== false;
        if (element._logScrollBound) return;
        element._logScrollBound = true;
        element.addEventListener('scroll', function () {
            const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
            element._autoStick = distance < 32;
        }, { passive: true });
    };

    ffui.scrollToBottom = function (element) {
        if (!element) return;
        if (element._autoStick === false) return;
        element.scrollTop = element.scrollHeight;
    };

    /* ── 下载（导出模板等）───────────────────────────────────────────── */
    ffui.saveTextFile = function (fileName, content, mimeType) {
        try {
            const blob = new Blob([content], { type: mimeType || 'application/json;charset=utf-8' });
            const url = URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
            return true;
        } catch (e) {
            return false;
        }
    };

    /* ── 通知 ────────────────────────────────────────────────────────── */
    ffui.requestNotificationPermission = async function () {
        if (!('Notification' in window)) return false;
        if (Notification.permission === 'granted') return true;
        if (Notification.permission === 'denied') return false;
        try {
            const result = await Notification.requestPermission();
            return result === 'granted';
        } catch (e) {
            return false;
        }
    };

    ffui.notify = function (title, body) {
        try {
            if (!('Notification' in window) || Notification.permission !== 'granted') return false;
            new Notification(title, { body: body || '' });
            return true;
        } catch (e) {
            return false;
        }
    };

    /* ── 键盘 ────────────────────────────────────────────────────────── */
    ffui.focusElement = function (element) {
        if (!element) return;
        try { element.focus({ preventScroll: false }); } catch (e) { }
    };

    // 按 Esc 关闭最上层的对话框（点击它的关闭按钮，由 Blazor 处理状态）
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape' && e.key !== 'Esc') return;
        const modals = document.querySelectorAll('.ffui-modal');
        if (modals.length === 0) return;
        const top = modals[modals.length - 1];
        const close = top.querySelector('.ffui-modal-header .ffui-icon-btn');
        if (close && !close.disabled) {
            e.preventDefault();
            close.click();
        }
    }, true);

    /* ── 防止文件拖放时浏览器跳转 ────────────────────────────────────── */
    document.addEventListener('dragover', function (e) { e.preventDefault(); }, false);
    document.addEventListener('drop', function (e) {
        // 只有在没有被组件处理时才阻止默认行为
        if (!e.defaultPrevented) e.preventDefault();
    }, false);

    // 首屏先把主题设上，避免深色模式闪白
    ffui.applyTheme(document.documentElement.getAttribute('data-theme-mode') || 'System');
    ffui.watchSystemTheme();

    window.ffui = ffui;
})();
