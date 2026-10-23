#!/usr/bin/env python3
"""Virtual keyboard + mouse over /dev/uinput, driven through a FIFO.

Commands (one per line):
  down|up|tap K[+K..] [ms]   press / release / press-and-release keys
  hold K[+K..] SECONDS       hold keys for a while
  look DX DY                 relative mouse move, sent in small steps
  click left|right|middle [ms] / mdown B / mup B   buttons on the relative device
  wheel N                    scroll wheel
  abs X Y                    absolute pointer move in screen pixels (2560x1440)
  aclick left|right [ms]     click on the absolute pointer
  sleep SECONDS
  ack TOKEN                  write TOKEN to the log once everything before it ran
  release                    release every held key / button
"""
import os, sys, struct, fcntl, time, signal

UI_SET_EVBIT, UI_SET_KEYBIT, UI_SET_RELBIT, UI_SET_ABSBIT = 0x40045564, 0x40045565, 0x40045566, 0x40045567
UI_DEV_CREATE, UI_DEV_DESTROY = 0x5501, 0x5502
EV_SYN, EV_KEY, EV_REL, EV_ABS = 0, 1, 2, 3
REL_X, REL_Y, REL_WHEEL = 0, 1, 8
ABS_X, ABS_Y = 0, 1
W, H = 2560, 1440
BTN = {'left': 0x110, 'right': 0x111, 'middle': 0x112}

KEYS = {'esc': 1, 'minus': 12, 'equal': 13, 'backspace': 14, 'tab': 15, 'enter': 28, 'ctrl': 29,
        'shift': 42, 'alt': 56, 'space': 57, 'up': 103, 'left': 105, 'right': 106, 'down': 108}
for i, c in enumerate('1234567890'):
    KEYS[c] = 2 + i
for start, row in ((16, 'qwertyuiop'), (30, 'asdfghjkl'), (44, 'zxcvbnm')):
    for i, c in enumerate(row):
        KEYS[c] = start + i
for i in range(10):
    KEYS['f%d' % (i + 1)] = 59 + i
KEYS['f11'], KEYS['f12'] = 87, 88

HERE = os.path.dirname(os.path.abspath(__file__))
FIFO = os.path.join(HERE, 'inp.fifo')
LOG = os.path.join(HERE, 'inp.log')


def mkdev(name, evbits, keys=(), rels=(), abss=(), absmax=None):
    fd = os.open('/dev/uinput', os.O_WRONLY | os.O_NONBLOCK)
    for e in evbits:
        fcntl.ioctl(fd, UI_SET_EVBIT, e)
    for k in keys:
        fcntl.ioctl(fd, UI_SET_KEYBIT, k)
    for r in rels:
        fcntl.ioctl(fd, UI_SET_RELBIT, r)
    for a in abss:
        fcntl.ioctl(fd, UI_SET_ABSBIT, a)
    amax = [0] * 64
    for a, v in (absmax or {}).items():
        amax[a] = v
    os.write(fd, struct.pack('80sHHHHI', name.encode(), 0x03, 0x1209, 0x7e57, 1, 0)
             + struct.pack('64i', *amax) + struct.pack('64i', *([0] * 64))
             + struct.pack('64i', *([0] * 64)) + struct.pack('64i', *([0] * 64)))
    fcntl.ioctl(fd, UI_DEV_CREATE)
    return fd


def emit(fd, etype, code, value):
    os.write(fd, struct.pack('llHHi', 0, 0, etype, code, value))


def syn(fd):
    emit(fd, EV_SYN, 0, 0)


rel_dev = mkdev('claude-virtual-kbm', [EV_KEY, EV_REL, EV_SYN],
                keys=list(KEYS.values()) + list(BTN.values()),
                rels=[REL_X, REL_Y, REL_WHEEL])
abs_dev = mkdev('claude-virtual-pointer', [EV_KEY, EV_ABS, EV_SYN],
                keys=list(BTN.values()), abss=[ABS_X, ABS_Y], absmax={ABS_X: W - 1, ABS_Y: H - 1})
held = set()


def log(msg):
    with open(LOG, 'a') as f:
        f.write(msg + '\n')


def key(name, val):
    code = KEYS[name]
    emit(rel_dev, EV_KEY, code, val)
    syn(rel_dev)
    (held.add if val else held.discard)(code)


def keys(spec, val):
    for k in spec.split('+'):
        key(k, val)
        time.sleep(0.005)


def button(dev, name, val):
    emit(dev, EV_KEY, BTN[name], val)
    syn(dev)
    code = BTN[name] + (1 << 12) * (dev == abs_dev)
    (held.add if val else held.discard)(code)


def look(dx, dy):
    n = max(1, int(max(abs(dx), abs(dy)) / 30))
    px = py = 0
    for i in range(1, n + 1):
        tx, ty = round(dx * i / n), round(dy * i / n)
        if tx - px:
            emit(rel_dev, EV_REL, REL_X, tx - px)
        if ty - py:
            emit(rel_dev, EV_REL, REL_Y, ty - py)
        syn(rel_dev)
        px, py = tx, ty
        time.sleep(0.006)


def release_all():
    for code in list(held):
        if code >= (1 << 12):
            emit(abs_dev, EV_KEY, code - (1 << 12), 0)
            syn(abs_dev)
        else:
            emit(rel_dev, EV_KEY, code, 0)
            syn(rel_dev)
    held.clear()


def run(line):
    p = line.split()
    if not p:
        return
    c, a = p[0], p[1:]
    if c == 'down':
        keys(a[0], 1)
    elif c == 'up':
        keys(a[0], 0)
    elif c == 'tap':
        keys(a[0], 1); time.sleep((float(a[1]) if len(a) > 1 else 60) / 1000); keys(a[0], 0)
    elif c == 'hold':
        keys(a[0], 1); time.sleep(float(a[1])); keys(a[0], 0)
    elif c == 'look':
        look(int(a[0]), int(a[1]))
    elif c == 'mdown':
        button(rel_dev, a[0], 1)
    elif c == 'mup':
        button(rel_dev, a[0], 0)
    elif c == 'click':
        button(rel_dev, a[0], 1); time.sleep((float(a[1]) if len(a) > 1 else 60) / 1000); button(rel_dev, a[0], 0)
    elif c == 'wheel':
        emit(rel_dev, EV_REL, REL_WHEEL, int(a[0])); syn(rel_dev)
    elif c == 'abs':
        emit(abs_dev, EV_ABS, ABS_X, max(0, min(W - 1, int(a[0]))))
        emit(abs_dev, EV_ABS, ABS_Y, max(0, min(H - 1, int(a[1]))))
        syn(abs_dev)
    elif c == 'aclick':
        button(abs_dev, a[0], 1); time.sleep((float(a[1]) if len(a) > 1 else 60) / 1000); button(abs_dev, a[0], 0)
    elif c == 'sleep':
        time.sleep(float(a[0]))
    elif c == 'release':
        release_all()
    elif c == 'ack':
        log('ack ' + a[0])
    else:
        log('unknown: ' + line)


def shutdown(*_):
    try:
        release_all()
        time.sleep(0.05)
        for d in (rel_dev, abs_dev):
            fcntl.ioctl(d, UI_DEV_DESTROY)
    finally:
        try:
            os.unlink(FIFO)
        except OSError:
            pass
        sys.exit(0)


signal.signal(signal.SIGTERM, shutdown)
signal.signal(signal.SIGINT, shutdown)

if not os.path.exists(FIFO):
    os.mkfifo(FIFO)
time.sleep(1.0)  # let the compositor pick up the new devices
log('ready')
fifo = os.open(FIFO, os.O_RDWR)
buf = b''
while True:
    chunk = os.read(fifo, 4096)
    if not chunk:
        time.sleep(0.05)
        continue
    buf += chunk
    while b'\n' in buf:
        raw, buf = buf.split(b'\n', 1)
        try:
            run(raw.decode().strip())
        except Exception as e:  # keep the daemon alive on a bad command
            log('error: %r in %r' % (e, raw))
