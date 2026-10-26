# Optional Chrome / Edge helper

The helper adds **Download link with ADM** to the right-click menu for HTTP/HTTPS links. Its toolbar popup also accepts a pasted URL. ADM opens a new-download form so you can review the name and destination before starting.

It does not intercept ordinary browser downloads, read webpages, export cookies, or extract videos. It only needs `contextMenus`, `nativeMessaging`, and local extension `storage` permissions. There are no remote services or host-access permissions.

## Connect it

1. Extract the ADM package to a permanent folder. Keep `ADM.exe` and `ADM.Bridge.exe` there.
2. Open `chrome://extensions` in Chrome or `edge://extensions` in Edge.
3. Enable **Developer mode**, choose **Load unpacked**, and select the package's `browser-extension` folder.
4. Copy the extension's 32-character ID from its card.
5. Open PowerShell in the ADM package folder and run:

```powershell
.\Install-BrowserHelper.ps1 -ExtensionId YOUR_EXTENSION_ID
```

The setup script registers only the current Windows user's native host for Chrome and Edge. It writes a manifest beside ADM and two ADM-specific registry entries under `HKCU\Software`. No administrator access is needed. If PowerShell policy blocks local scripts, review the script and follow your organization's approved execution-policy process.

6. Right-click a direct file link and choose **Download link with ADM**, or paste it in the extension popup. ADM should open with that URL ready to review.

If you load the extension from different paths in Chrome and Edge, the extension IDs may differ. Use the same folder to keep the ID consistent. The supplied setup script registers one extension ID at a time; rerunning it replaces the allowed ID. Moving ADM or the extension later requires registering again.

## Remove it

Run `Uninstall-BrowserHelper.ps1` in the package folder, then remove the extension in the browser. The script only removes ADM's two native-host registry keys. Your download library is unaffected.

## Troubleshooting

- **Native host not found:** rerun setup with the current extension ID and confirm the app folder has not moved.
- **Access forbidden:** compare the loaded extension ID with `allowed_origins` in `com.adm.download_manager.json`.
- **Runtime missing:** use the standalone package, or install .NET 10 Desktop Runtime for the compact package.
- **Link downloads HTML or fails:** use a direct file URL. Login sessions and browser cookies are not transferred.
- **ADM is already open:** the helper forwards the URL to that instance over a pipe restricted to your Windows account.

The native host accepts one JSON request, validates its HTTP(S) URL, launches ADM with that URL as a separate argument, and returns a length-prefixed JSON result. It does not execute commands supplied by the webpage. Native host behavior follows [Chromium's native messaging protocol](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging).
