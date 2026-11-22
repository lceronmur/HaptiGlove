"""
HaptiGlove - Rastreador de mano con MediaPipe -> Unity (UDP).

Unity lo arranca solo al dar Play (script HandTrackerUDP). También se puede correr a mano:
    pip3 install mediapipe opencv-python
    python3 hand_tracker.py [--camera 0] [--port 5065] [--no-window]

Envía por UDP una línea por cuadro:  found,x,y,size
  x, y : centro de la palma normalizado 0..1 (x en espejo, y hacia arriba)
  size : tamaño aparente de la palma (relativo a la altura de la imagen)
"""
import argparse
import math
import os
import socket
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
if getattr(sys, "frozen", False):
    # Version .exe (hand_tracking/hand_tracker_win/hand_tracker.exe): el modelo esta en hand_tracking/
    HERE = os.path.dirname(os.path.dirname(sys.executable))
MODEL_PATH = os.path.join(HERE, "hand_landmarker.task")
MODEL_URL = ("https://storage.googleapis.com/mediapipe-models/hand_landmarker/"
             "hand_landmarker/float16/latest/hand_landmarker.task")

PALM_IDS = [0, 5, 9, 13, 17]
SIZE_PAIRS = [(0, 5), (0, 17), (5, 17), (0, 9)]
CONNECTIONS = [(0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8),
               (5, 9), (9, 10), (10, 11), (11, 12), (9, 13), (13, 14), (14, 15),
               (15, 16), (13, 17), (0, 17), (17, 18), (18, 19), (19, 20)]


def log(msg):
    print(msg, flush=True)


def ensure_model():
    if os.path.exists(MODEL_PATH):
        return True
    log("Descargando modelo de MediaPipe...")
    import ssl
    try:
        import certifi
        ctx = ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        ctx = ssl.create_default_context()
    try:
        tmp = MODEL_PATH + ".part"
        with urllib.request.urlopen(MODEL_URL, context=ctx) as r, open(tmp, "wb") as f:
            f.write(r.read())
        os.replace(tmp, MODEL_PATH)
        log("Modelo listo.")
        return True
    except Exception as e:  # noqa: BLE001
        log(f"ERROR: no se pudo descargar el modelo ({e}). "
            f"Descárgalo con el navegador desde {MODEL_URL} y guárdalo como {MODEL_PATH}")
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--camera", type=int, default=0)
    ap.add_argument("--port", type=int, default=5065)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--no-window", action="store_true")
    ap.add_argument("--video-port", type=int, default=0,
                    help="Si es > 0, envía la imagen (JPEG pequeño) a Unity por este puerto UDP.")
    args = ap.parse_args()

    try:
        import cv2
        import mediapipe as mp
        from mediapipe.tasks import python as mp_python
        from mediapipe.tasks.python import vision
    except ImportError as e:
        log(f"ERROR: falta una librería ({e.name}). Instala con: "
            f"{sys.executable} -m pip install mediapipe opencv-python")
        sys.exit(3)

    if not ensure_model():
        sys.exit(2)

    landmarker = vision.HandLandmarker.create_from_options(vision.HandLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=MODEL_PATH),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=1,
        # Umbrales bajos para que reconozca la mano con el guante puesto (sin piel visible)
        min_hand_detection_confidence=0.3,
        min_hand_presence_confidence=0.3,
        min_tracking_confidence=0.3,
    ))

    cap = cv2.VideoCapture(args.camera)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    cap.set(cv2.CAP_PROP_FPS, 30)
    if not cap.isOpened():
        log(f"ERROR: no se pudo abrir la cámara {args.camera}. "
            "Revisa el permiso de cámara en Ajustes del Sistema > Privacidad y seguridad > Cámara.")
        sys.exit(4)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    parent = os.getppid()
    t0 = time.time()
    last_ts = -1
    had_hand = None
    win = "HaptiGlove - MediaPipe (q para cerrar)"
    log(f"Rastreador listo: cámara {args.camera} -> UDP {args.host}:{args.port}")

    while True:
        if os.getppid() != parent:          # Unity se cerró: salir también
            break
        ok, frame = cap.read()
        if not ok:
            time.sleep(0.01)
            continue
        frame = cv2.flip(frame, 1)
        h, w = frame.shape[:2]
        mp_image = mp.Image(image_format=mp.ImageFormat.SRGB,
                            data=cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
        ts = max(int((time.time() - t0) * 1000), last_ts + 1)
        last_ts = ts
        result = landmarker.detect_for_video(mp_image, ts)

        hand = bool(result.hand_landmarks)
        if hand:
            lm = result.hand_landmarks[0]
            cx = sum(lm[i].x for i in PALM_IDS) / len(PALM_IDS)
            cy = sum(lm[i].y for i in PALM_IDS) / len(PALM_IDS)
            size = sum(math.hypot((lm[a].x - lm[b].x) * w, (lm[a].y - lm[b].y) * h)
                       for a, b in SIZE_PAIRS) / len(SIZE_PAIRS) / h
            msg = f"1,{cx:.5f},{1.0 - cy:.5f},{size:.5f}"
        else:
            msg = "0,0,0,0"
        try:
            sock.sendto(msg.encode("ascii"), (args.host, args.port))
        except OSError:
            pass

        if hand != had_hand:
            log("Mano detectada" if hand else "Mano perdida")
            had_hand = hand

        draw = (not args.no_window) or args.video_port > 0
        if draw:
            if hand:
                pts = [(int(p.x * w), int(p.y * h)) for p in lm]
                for a, b in CONNECTIONS:
                    cv2.line(frame, pts[a], pts[b], (0, 255, 0), 2)
                for p in pts:
                    cv2.circle(frame, p, 3, (0, 0, 255), -1)
                cv2.circle(frame, (int(cx * w), int(cy * h)), 8, (255, 0, 255), -1)
            cv2.putText(frame, "MANO OK" if hand else "sin mano", (10, 30),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 2)
        if args.video_port > 0:
            # macOS limita los paquetes UDP a ~9 KB: bajar calidad hasta que quepa.
            small = cv2.resize(frame, (288, 216))
            for q in (65, 50, 40, 30, 20):
                ok_jpg, jpg = cv2.imencode(".jpg", small, [cv2.IMWRITE_JPEG_QUALITY, q])
                if ok_jpg and len(jpg) <= 9000:
                    try:
                        sock.sendto(jpg.tobytes(), (args.host, args.video_port))
                    except OSError:
                        pass
                    break
        if not args.no_window:
            cv2.imshow(win, frame)
            if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                break

    cap.release()
    if not args.no_window:
        cv2.destroyAllWindows()
    landmarker.close()
    log("Rastreador cerrado.")


if __name__ == "__main__":
    main()
