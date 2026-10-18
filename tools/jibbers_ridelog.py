"""Log the rider's speed vs distance in a running Jibbers (hotload mod), from the camera, which
follows the rider. Read-only: only the mod's `status` command is used.

    python tools/jibbers_ridelog.py [seconds] [out.csv]
Columns: t (s), x, y, z, distance along the ground (m), speed (km/h, 0.5 s window).
"""
import csv
import math
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from hotload import request  # noqa: E402


def cam():
    s = request({"cmd": "status"}, timeout=5)
    c = s["camera"][0]["position"]
    return c["x"], c["y"], c["z"]


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 30
    out = sys.argv[2] if len(sys.argv) > 2 else "jibbers_ride.csv"
    rows, t0, dist = [], time.time(), 0.0
    while time.time() - t0 < secs:
        t = time.time() - t0
        p = cam()
        if rows:
            q = rows[-1]
            dist += math.dist(p, (q[1], q[2], q[3]))
        rows.append((t, *p, dist))
        time.sleep(0.1)
    with open(out, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["t", "x", "y", "z", "dist", "kmh"])
        for i, r in enumerate(rows):
            j = max(0, i - 5)
            dt = r[0] - rows[j][0]
            v = (r[4] - rows[j][4]) / dt * 3.6 if dt > 0 else 0
            w.writerow([round(r[0], 2), *(round(x, 2) for x in r[1:5]), round(v, 1)])
    print(f"wrote {len(rows)} samples to {out}")


if __name__ == "__main__":
    main()
