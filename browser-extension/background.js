const HOST = 'com.adm.download_manager';
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: 'send-adm', title: 'Download link with ADM', contexts: ['link'], targetUrlPatterns: ['http://*/*', 'https://*/*'] });
});
async function send(url) {
  try {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) throw new Error('Use a direct HTTP or HTTPS file link.');
    const response = await chrome.runtime.sendNativeMessage(HOST, { url: parsed.href });
    if (!response?.ok) throw new Error(response?.error || 'ADM did not accept this link.');
    await chrome.action.setBadgeText({ text: '' });
    await chrome.storage.local.remove('lastError');
    return { ok: true };
  } catch (error) {
    const detail = String(error.message || error);
    const message = detail.includes('native messaging host') || detail.includes('Native host')
      ? 'Connect the helper first: run Install-BrowserHelper.ps1 with this extension’s ID. See BROWSER-SETUP.md.' : detail;
    await chrome.action.setBadgeText({ text: '!' });
    await chrome.action.setBadgeBackgroundColor({ color: '#b55940' });
    await chrome.storage.local.set({ lastError: message });
    return { ok: false, error: message };
  }
}
chrome.contextMenus.onClicked.addListener((info) => { if (info.menuItemId === 'send-adm') void send(info.linkUrl); });
chrome.runtime.onMessage.addListener((message, _sender, respond) => {
  if (message?.type !== 'send') return false;
  void send(message.url).then(respond);
  return true;
});
