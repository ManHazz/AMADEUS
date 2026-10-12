"""Time the streaming /api/converse: time-to-first-audio vs total."""
import sys, time, struct, json, urllib.request

URL = "http://localhost:5000/api/converse"
TOKEN = sys.argv[1] if len(sys.argv) > 1 else open(".env").read().split("VOICE_AGENT_TOKEN=")[1].split()[0]
WAV = sys.argv[2] if len(sys.argv) > 2 else "/tmp/q.wav"

body = open(WAV, "rb").read()
boundary = "----vt"
data = (
    f"--{boundary}\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"q.wav\"\r\n"
    f"Content-Type: audio/wav\r\n\r\n"
).encode() + body + f"\r\n--{boundary}--\r\n".encode()

req = urllib.request.Request(URL, data=data, headers={
    "X-Access-Token": TOKEN,
    "Content-Type": f"multipart/form-data; boundary={boundary}",
})

t0 = time.time()
first_audio = None
n_audio = 0
buf = b""
last_reply = ""
with urllib.request.urlopen(req) as r:
    while True:
        piece = r.read(4096)
        if not piece:
            break
        buf += piece
        while len(buf) >= 5:
            ftype = buf[0]
            (length,) = struct.unpack(">I", buf[1:5])
            if len(buf) < 5 + length:
                break
            payload, buf = buf[5:5 + length], buf[5 + length:]
            if ftype == 1:
                m = json.loads(payload)
                last_reply = m["reply"]
                tag = "done" if m["done"] else "meta"
                print(f"  [{time.time()-t0:5.2f}s] {tag}: {m['reply'][:80]!r}")
            elif ftype == 2:
                n_audio += 1
                if first_audio is None:
                    first_audio = time.time() - t0
                print(f"  [{time.time()-t0:5.2f}s] audio #{n_audio}  ({length} bytes)")

print(f"\ntime to first audio : {first_audio:.2f}s")
print(f"total               : {time.time()-t0:.2f}s   ({n_audio} audio chunks)")
print(f"reply               : {last_reply!r}")
