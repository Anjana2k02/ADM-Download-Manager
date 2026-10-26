const input = document.getElementById('url');
const button = document.getElementById('send');
const status = document.getElementById('status');
chrome.storage.local.get('lastError').then(({ lastError }) => { if (lastError) status.textContent = lastError; });
button.addEventListener('click', async () => {
  button.disabled = true; status.textContent = 'Opening ADM…';
  try {
    const response = await chrome.runtime.sendMessage({ type: 'send', url: input.value.trim() });
    status.textContent = response?.ok ? 'Sent. Confirm your download in ADM.' : response?.error || 'Could not reach ADM.';
  } catch (error) { status.textContent = error.message; }
  finally { button.disabled = false; }
});
