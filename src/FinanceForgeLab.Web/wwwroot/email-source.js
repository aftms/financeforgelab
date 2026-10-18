export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}
