"""Validate native-message framing without installing a browser extension."""
import json
import pathlib
import struct
import subprocess

root = pathlib.Path(__file__).resolve().parents[1]
host = root / "src/ADM.Bridge/bin/Release/net10.0-windows/ADM.Bridge.exe"
if not host.exists():
    raise SystemExit("Build ADM.sln in Release first.")

def frame(message):
    body = json.dumps(message).encode()
    return struct.pack("<I", len(body)) + body

def exchange(data):
    result = subprocess.run([str(host)], input=data, capture_output=True, timeout=10,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    assert len(result.stdout) >= 4, result.stderr.decode(errors="replace")
    length, = struct.unpack("<I", result.stdout[:4])
    assert length == len(result.stdout) - 4, "Native host emitted an invalid frame"
    response = json.loads(result.stdout[4:])
    assert response["ok"] is False
    assert response.get("error")
    return response

for label, data in [
    ("file scheme rejection", frame({"url": "file:///C:/Windows/win.ini"})),
    ("credentials rejection", frame({"url": "https://user:password@example.com/file"})),
    ("oversize message rejection", struct.pack("<I", 65537)),
    ("truncated input handling", b"\x01\x00"),
    ("malformed JSON handling", struct.pack("<I", 1) + b"{"),
    ("missing ADM executable guidance", frame({"url": "https://example.com/file.zip"})),
]:
    response = exchange(data)
    if "missing ADM" in label:
        assert "ADM.exe" in response["error"]
    print("PASS ", label)
