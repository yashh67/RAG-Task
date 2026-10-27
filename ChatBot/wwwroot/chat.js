// Small helpers for the chat page (called from Chat.razor via JS interop).
window.chat = {
    // Enter sends, Shift+Enter inserts a new line; the textarea grows with its content.
    initComposer(textarea) {
        if (!textarea || textarea.dataset.chatInit) return;
        textarea.dataset.chatInit = "1";

        const resize = () => {
            textarea.style.height = "auto";
            textarea.style.height = Math.min(textarea.scrollHeight, 200) + "px";
        };
        textarea.addEventListener("input", resize);
        textarea.addEventListener("keydown", e => {
            if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
                e.preventDefault();
                textarea.form?.requestSubmit();
                setTimeout(resize, 0);
            }
        });
    },

    // Scrolls to the bottom when forced, or when the user is already near the bottom.
    scrollToBottom(el, force) {
        if (!el) return;
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
        if (force || nearBottom) el.scrollTop = el.scrollHeight;
    },

    focus(el) { el?.focus(); }
};
