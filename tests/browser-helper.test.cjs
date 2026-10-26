const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const folder = path.join(__dirname, '..', 'browser-extension');
const manifest = JSON.parse(fs.readFileSync(path.join(folder, 'manifest.json'), 'utf8'));
let onMessage, onContextMenu, onInstall, sent, badge, storage = {}, nativeError;
const chrome = {
  runtime: {
    onInstalled: { addListener(fn) { onInstall = fn; } },
    onMessage: { addListener(fn) { onMessage = fn; } },
    async sendNativeMessage(host, request) { if (nativeError) throw new Error(nativeError); sent = { host, request }; return { ok: true }; },
  },
  contextMenus: { create(info) { assert.deepEqual(Array.from(info.contexts), ['link']); }, onClicked: { addListener(fn) { onContextMenu = fn; } } },
  action: { async setBadgeText({ text }) { badge = text; }, async setBadgeBackgroundColor() {} },
  storage: { local: { async set(value) { Object.assign(storage, value); }, async remove(key) { delete storage[key]; } } },
};
vm.runInNewContext(fs.readFileSync(path.join(folder, 'background.js'), 'utf8'), { chrome, URL, Error, String });
function request(url) { return new Promise(resolve => { assert.equal(onMessage({ type: 'send', url }, {}, resolve), true); }); }
(async () => {
  assert.equal(manifest.manifest_version, 3); assert.equal(manifest.host_permissions, undefined); onInstall();
  console.log('PASS  MV3 helper requests no webpage permissions');
  let result = await request('https://example.com/file.zip'); assert.equal(result.ok, true);
  assert.equal(sent.host, 'com.adm.download_manager'); assert.equal(sent.request.url, 'https://example.com/file.zip'); assert.equal(badge, '');
  console.log('PASS  Valid direct links reach the native host');
  for (const url of ['javascript:alert(1)', 'file:///C:/Windows/win.ini', 'https://user:secret@example.com/a', 'not a url']) {
    result = await request(url); assert.equal(result.ok, false);
  }
  console.log('PASS  Unsafe protocols and embedded credentials are rejected');
  nativeError = 'Specified native messaging host not found.'; result = await request('https://example.com/file.zip');
  assert.equal(result.ok, false); assert.match(result.error, /Install-BrowserHelper/); assert.equal(badge, '!'); assert.ok(storage.lastError);
  console.log('PASS  Missing host shows actionable installation guidance');
  nativeError = undefined; onContextMenu({ menuItemId: 'send-adm', linkUrl: 'https://example.com/context.zip' });
  await new Promise(resolve => setImmediate(resolve)); assert.equal(sent.request.url, 'https://example.com/context.zip');
  assert.equal(storage.lastError, undefined); assert.equal(onMessage({ type: 'unknown' }, {}, () => {}), false);
  console.log('PASS  Context menu forwards links and clears prior errors');
})().catch(error => { console.error(error); process.exitCode = 1; });
