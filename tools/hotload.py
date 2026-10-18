"""Talk to the in-game hotload mod (crates/jibbers-hotload) from the command line.

    python tools/hotload.py ping
    python tools/hotload.py status
    python tools/hotload.py raw '{"cmd":"inspect","image":"Assembly-CSharp.dll","class":"eg","field":"wrd"}'
"""

import json
import socket
import sys

PORT = 47820


def request(req: dict, timeout: float = 180.0) -> dict:
    with socket.create_connection(("127.0.0.1", PORT), timeout=timeout) as s:
        s.sendall((json.dumps(req) + "\n").encode())
        buf = b""
        while not buf.endswith(b"\n"):
            chunk = s.recv(1 << 16)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf)


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    cmd = argv[0]
    if cmd == "raw":
        req = json.loads(argv[1])
    else:
        req = {"cmd": cmd}
    try:
        reply = request(req)
    except ConnectionRefusedError:
        print("The game isn't running with the hotload mod (nothing on 127.0.0.1:%d)." % PORT, file=sys.stderr)
        return 1
    print(json.dumps(reply, indent=2))
    return 0 if reply.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
