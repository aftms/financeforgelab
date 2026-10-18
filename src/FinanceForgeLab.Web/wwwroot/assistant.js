export function attach() {
    const area = document.getElementById('assistant-compose');
    const input = document.getElementById('assistant-files');
    if (!area || !input || area.dataset.filesAttached) return;
    area.dataset.filesAttached = 'true';
    const receive = (files) => {
        if (input.disabled || !files.length) return;
        const transfer = new DataTransfer();
        for (const file of files) transfer.items.add(file);
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };
    area.addEventListener('paste', event => {
        const files = [...(event.clipboardData?.files || [])];
        if (files.length) { event.preventDefault(); receive(files); }
    });
    area.addEventListener('dragover', event => { if (event.dataTransfer?.types.includes('Files')) event.preventDefault(); });
    area.addEventListener('drop', event => {
        if (event.dataTransfer?.files.length) { event.preventDefault(); receive([...event.dataTransfer.files]); }
    });
}
