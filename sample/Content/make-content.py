# make-content.py: the sample's art and sound, under the repository's MIT license. Writes, beside this
# script, Textures/spritesheet.png (16 variants of the sample's character, Source/character.png) and
# Textures/character.png (the character itself), LaunchScreen/logo.png, procedural, Sound/boing.wav,
# Sound/chime.wav and Music/loop.wav, synthesized (tones, noise and filters computed from numbers, with
# fixed seeds), and, given a font file,
# Textures/ascii.png: the sample's is rendered from JetBrains Mono Medium (Copyright 2020 The
# JetBrains Mono Project Authors, https://github.com/JetBrains/JetBrainsMono, under the SIL Open Font
# License 1.1), a monospaced font, as PixelText draws every character in a cell of the same width.
#
#   python sample/Content/make-content.py [JetBrainsMono-Medium.ttf]
#
# Needs numpy and Pillow.
import math, os, sys, wave
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SS = 4  # supersampling, for antialiased edges


def path(*parts):
    p = os.path.join(HERE, *parts)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    return p


# The character's variants, for the sprite sheet: each with a bandana of another color and an
# accessory (a hat, glasses, something in the raised hand), so every sprite is different, and every one
# is asymmetric, as flipping shows. Their bodies stay white, so a tint shows as itself. Coordinates are
# Source/character.png's (1115 x 1251, its outline about 22 px), on a canvas with room for the hats
# above and the balloon to the right, the same for every variant so they share one scale.
OUTLINE = (42, 27, 46, 255)
LINE = 22
PAD_L, PAD_T, PAD_R, PAD_B = 40, 150, 70, 30


def recolor_bandana(src, turns):
    # The bandana's saturated reds and oranges turned around the hue circle; the mouth's pink, whose
    # blue is above its green, kept
    if not turns:
        return src
    a = np.asarray(src).astype(np.float32) / 255
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    mx, mn = a[..., :3].max(axis=2), a[..., :3].min(axis=2)
    band = (r == mx) & (g >= b - 0.03) & (mx - mn > 0.35 * mx) & (mx > 0.25)
    hsv = np.asarray(src.convert('RGB').convert('HSV')).astype(np.float32)
    hsv[..., 0] = np.where(band, (hsv[..., 0] + turns * 255) % 255, hsv[..., 0])
    shifted = Image.fromarray(hsv.astype(np.uint8), 'HSV').convert('RGB')
    return Image.merge('RGBA', (*shifted.split(), src.getchannel('A')))


def at(x, y):
    return (x + PAD_L, y + PAD_T)


def shape(d, pts, fill):
    pts = [at(*p) for p in pts]
    d.polygon(pts, fill=fill)
    d.line(pts + [pts[0]], fill=OUTLINE, width=LINE, joint='curve')


def oval(d, cx, cy, rx, ry, fill, width=LINE):
    x, y = at(cx, cy)
    d.ellipse([x - rx, y - ry, x + rx, y + ry], fill=fill, outline=OUTLINE, width=width)


def stroke(d, pts, width=LINE, fill=OUTLINE):
    d.line([at(*p) for p in pts], fill=fill, width=width, joint='curve')


def star(cx, cy, r, inner=0.45):
    return [(cx + (r if i % 2 == 0 else r * inner) * math.cos(math.radians(-90 + i * 36)),
             cy + (r if i % 2 == 0 else r * inner) * math.sin(math.radians(-90 + i * 36))) for i in range(10)]


def cap(d):
    shape(d, [(250, 330), (300, 210), (420, 150), (560, 175), (620, 260), (600, 300), (330, 330)], (70, 130, 220, 255))
    shape(d, [(250, 330), (120, 360), (130, 395), (330, 360)], (60, 110, 200, 255))


def crown(d):
    shape(d, [(300, 230), (300, 90), (370, 160), (430, 60), (490, 150), (550, 70), (560, 220)], (250, 200, 60, 255))
    oval(d, 430, 185, 22, 22, (230, 70, 90, 255), 10)


def bow(d):
    shape(d, [(720, 110), (580, 20), (585, 210)], (245, 120, 170, 255))
    shape(d, [(720, 110), (860, 20), (855, 210)], (245, 120, 170, 255))
    oval(d, 720, 112, 48, 48, (250, 150, 190, 255))


def glasses(d):
    for cx, cy in ((490, 385), (705, 365)):
        oval(d, cx, cy, 95, 95, (200, 230, 255, 90), 18)
    stroke(d, [(585, 380), (612, 370)], 18)
    stroke(d, [(395, 395), (300, 380)], 18)


def sunglasses(d):
    for cx, cy in ((490, 385), (705, 365)):
        x, y = at(cx, cy)
        d.rounded_rectangle([x - 100, y - 60, x + 100, y + 60], radius=40, fill=(25, 25, 35, 255), outline=OUTLINE, width=18)
    stroke(d, [(590, 380), (605, 375)], 22)


def top_hat(d):
    shape(d, [(330, 220), (310, -60), (560, -80), (560, 190)], (45, 45, 60, 255))
    shape(d, [(250, 250), (600, 200), (610, 240), (260, 290)], (45, 45, 60, 255))
    shape(d, [(318, 120), (556, 98), (558, 150), (322, 170)], (220, 60, 70, 255))


def flower(d):
    stroke(d, [(905, 515), (960, 330)], 16, (60, 150, 70, 255))
    for k in range(6):
        a = math.radians(k * 60)
        oval(d, 965 + 70 * math.cos(a), 270 + 70 * math.sin(a), 55, 55, (250, 150, 200, 255), 14)
    oval(d, 965, 270, 45, 45, (255, 220, 80, 255), 14)


def balloon(d):
    stroke(d, [(910, 505), (960, 380), (1010, 300)], 8)
    oval(d, 1040, 160, 115, 140, (90, 190, 240, 255))
    shape(d, [(1020, 300), (1060, 300), (1040, 325)], (90, 190, 240, 255))


def wand(d):
    stroke(d, [(905, 515), (1010, 280)], 24, (150, 100, 60, 255))
    shape(d, star(1030, 230, 120), (255, 220, 70, 255))


def party_hat(d):
    shape(d, [(290, 250), (420, -60), (560, 200)], (120, 200, 120, 255))
    for k in (0.3, 0.6):
        y = -60 + k * 300
        stroke(d, [(420 - k * 130 + 10, y), (420 + k * 140 - 10, y - 12)], 20, (255, 255, 255, 255))
    oval(d, 420, -75, 40, 40, (250, 120, 160, 255))


def eyepatch(d):
    stroke(d, [(270, 300), (760, 470)], 18)
    oval(d, 492, 385, 82, 88, (25, 25, 35, 255), 18)


def headphones(d):
    x0, y0 = at(240, 120)
    x1, y1 = at(890, 700)
    d.arc([x0, y0, x1, y1], 195, 345, fill=(60, 60, 75, 255), width=46)
    d.arc([x0, y0, x1, y1], 195, 345, fill=OUTLINE, width=12)
    oval(d, 250, 440, 60, 95, (230, 80, 90, 255))
    oval(d, 880, 410, 60, 95, (230, 80, 90, 255))


def sprout(d):
    stroke(d, [(640, 20), (648, -40)], 18, (60, 150, 70, 255))
    shape(d, [(648, -20), (560, -95), (515, -25), (595, 5)], (110, 200, 100, 255))
    shape(d, [(648, -40), (760, -110), (800, -35), (700, 0)], (110, 200, 100, 255))


def wizard_hat(d):
    shape(d, [(240, 270), (380, 40), (540, -110), (480, 60), (630, 235)], (120, 90, 200, 255))
    shape(d, star(430, 150, 62), (255, 220, 70, 255))


def flag(d):
    stroke(d, [(905, 520), (930, 120)], 18, (150, 100, 60, 255))
    shape(d, [(930, 130), (1110, 180), (928, 250)], (250, 170, 60, 255))


# (bandana turns, accessory, drawn behind the character); the hero, plain, is the third in the third row
VARIANTS = [
    (0.55, cap, False), (0.10, crown, False), (0.85, bow, False), (0.30, glasses, False),
    (0.62, sunglasses, False), (0.0, top_hat, False), (0.40, flower, True), (0.75, balloon, True),
    (0.20, wand, True), (0.92, party_hat, False), (0.0, None, False), (0.48, eyepatch, False),
    (0.68, headphones, False), (0.33, sprout, False), (0.15, wizard_hat, False), (0.80, flag, True),
]


def character_variant(src, turns, accessory, behind):
    c = Image.new('RGBA', (src.width + PAD_L + PAD_R, src.height + PAD_T + PAD_B), (0, 0, 0, 0))
    if accessory and behind:
        accessory(ImageDraw.Draw(c))
    c.alpha_composite(recolor_bandana(src, turns), (PAD_L, PAD_T))
    if accessory and not behind:
        accessory(ImageDraw.Draw(c))
    return c


def fit(img, w, h):
    # Fitted in a w x h box and centered, premultiplied while resizing so the transparent edges
    # don't bleed dark
    scale = min(w / img.width, h / img.height)
    size = (round(img.width * scale), round(img.height * scale))
    fitted = img.convert('RGBa').resize(size, Image.LANCZOS).convert('RGBA')
    box = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    box.alpha_composite(fitted, ((w - size[0]) // 2, (h - size[1]) // 2))
    return box


def character(w, h):
    # The sample's character (Source/character.png), fitted in a w x h box
    return fit(Image.open(os.path.join(HERE, 'Source', 'character.png')).convert('RGBA'), w, h)


def images():
    # The sprite sheet: the character's 16 variants in 256 x 256 cells, each fitted in 240 x 248 at
    # (8, 4) in its cell, so sprite i's source rectangle is (i % 4 * 256 + 8, i / 4 * 256 + 4, 240, 248)
    src = Image.open(os.path.join(HERE, 'Source', 'character.png')).convert('RGBA')
    sheet = Image.new('RGBA', (1024, 1024), (0, 0, 0, 0))
    for i, (turns, accessory, behind) in enumerate(VARIANTS):
        col, row = i % 4, i // 4
        sheet.alpha_composite(fit(character_variant(src, turns, accessory, behind), 240, 248), (col * 256 + 8, row * 256 + 4))
    sheet.save(path('Textures', 'spritesheet.png'), optimize=True)
    # The character on its own, an odd size
    character(211, 237).save(path('Textures', 'character.png'), optimize=True)
    logo()


def logo():
    # The launch screen's logo: a play symbol in a rounded square, with a soft gradient
    W = 512 * SS
    y = np.linspace(0, 1, W)[:, None] * np.ones((1, W))
    grad = np.dstack([90 + 40 * (1 - y), 120 + 60 * (1 - y), 230 - 30 * y, np.full((W, W), 255)]).astype(np.uint8)
    mask = Image.new('L', (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=W * 0.22, fill=255)
    logo = Image.fromarray(grad, 'RGBA'); logo.putalpha(mask)
    d = ImageDraw.Draw(logo)
    c = W / 2
    d.polygon([(c - W * 0.14, c - W * 0.2), (c - W * 0.14, c + W * 0.2), (c + W * 0.22, c)], fill=(255, 255, 255, 255))
    logo.resize((512, 512), Image.LANCZOS).save(path('LaunchScreen', 'logo.png'), optimize=True)


def write(name, x, rate, bits):
    x = np.clip(np.atleast_2d(x), -1, 1)
    with wave.open(name, 'wb') as w:
        w.setnchannels(x.shape[0]); w.setframerate(rate); w.setsampwidth(bits // 8)
        inter = x.T.reshape(-1)
        if bits == 16:
            w.writeframes(np.round(inter * 32767).astype('<i2').tobytes())
        else:
            v = np.ascontiguousarray(np.round(inter * 8388607).astype('<i4'))
            w.writeframes(v.view(np.uint8).reshape(-1, 4)[:, :3].tobytes())


def tone(freq, n, rate, harmonics, decay):
    t = np.arange(n) / rate
    return sum(a * np.sin(2 * np.pi * freq * k * t) * np.exp(-t / (decay / k ** 0.5)) for k, a in harmonics)


def sounds():
    R = 44100
    # A boing: a falling, wobbling pitch with a quick decay
    n = int(1.05 * R); t = np.arange(n) / R
    f = 180 + 420 * np.exp(-t * 6) * (1 + 0.15 * np.sin(2 * np.pi * 14 * t))
    x = (np.sin(2 * np.pi * np.cumsum(f) / R) + 0.3 * np.sin(4 * np.pi * np.cumsum(f) / R)) * np.exp(-t * 3.2)
    x[:200] *= np.linspace(0, 1, 200)
    write(path('Sound', 'boing.wav'), x * 0.8 / np.max(np.abs(x)), R, 24)
    # A chime: inharmonic bell partials ringing out
    n = int(3.5 * R)
    partials = [(1.0, 1.0), (2.76, 0.5), (5.4, 0.25), (8.93, 0.12)]
    x = sum(a * np.sin(2 * np.pi * 660 * p * np.arange(n) / R) * np.exp(-np.arange(n) / R / (1.4 / p ** 0.7)) for p, a in partials)
    x[:100] *= np.linspace(0, 1, 100)
    write(path('Sound', 'chime.wav'), x * 0.8 / np.max(np.abs(x)), R, 24)


def music():
    # A loop at 120 beats a minute: two seconds of introduction, then 12 seconds that repeat
    # (samples 88 200 to 617 400 at 44.1 kHz, the sample's LoopPoint and LoopEnd). Notes ringing
    # past the loop's end are wrapped round to its start, so the loop is seamless.
    R = 44100; beat = R // 2; intro, loop = 4 * beat, 24 * beat
    rng = np.random.default_rng(3)
    out = np.zeros((2, intro + loop + 4 * beat))

    def add(x, at, pan=0.5, gain=1.0):
        out[0, at:at + len(x)] += x * gain * (1 - pan); out[1, at:at + len(x)] += x * gain * pan

    chords = [(220.0, 261.63, 329.63), (174.61, 220.0, 261.63), (196.0, 246.94, 293.66), (164.81, 207.65, 246.94)]
    for k, f in enumerate(chords[0]):  # the introduction: one swelling chord
        x = tone(f, intro, R, [(1, 1), (2, 0.3)], 3.0) * np.linspace(0, 1, intro) ** 2
        add(x, 0, 0.3 + 0.2 * k, 0.12)
    melody = [659.25, 0, 587.33, 523.25, 587.33, 0, 659.25, 783.99, 659.25, 0, 523.25, 440.0]
    for b in range(24):
        at = intro + b * beat
        chord = chords[(b // 6) % 4]
        if b % 6 == 0:
            for k, f in enumerate(chord):
                add(tone(f, 6 * beat, R, [(1, 1), (2, 0.25), (3, 0.1)], 1.6), at, 0.3 + 0.2 * k, 0.1)
        add(tone(chord[0] / 2, beat, R, [(1, 1), (2, 0.5), (3, 0.2)], 0.25), at, 0.5, 0.35)
        m = melody[b % len(melody)]
        if m:
            add(tone(m, beat, R, [(1, 1), (2, 0.35), (3, 0.15), (4, 0.06)], 0.18), at, 0.65, 0.2)
        for half in (0, beat // 2):
            h = rng.standard_normal(R // 25) * np.exp(-np.arange(R // 25) / (R * 0.006))
            h = np.diff(h, prepend=0)
            add(h, at + half, 0.7, 0.05 if half else 0.08)
    tail = out[:, intro + loop:].copy(); out = out[:, :intro + loop]
    out[:, intro:intro + tail.shape[1]] += tail
    write(path('Music', 'loop.wav'), out * 0.85 / np.max(np.abs(out)), R, 16)


def ascii_atlas(font_file):
    # Characters 32 to 127 in 16 columns of 20 x 32 cells, white on transparent (PixelText). Each
    # glyph stays inside its cell (28 px, baseline 25 px down, for JetBrains Mono Medium), or a
    # neighbor's source rectangle would show a piece of it
    font = ImageFont.truetype(font_file, 28)
    im = Image.new('RGBA', (16 * 20, 6 * 32), (255, 255, 255, 0))
    d = ImageDraw.Draw(im)
    for code in range(32, 128):
        i = code - 32
        ch = chr(code) if code < 127 else '•'
        d.text(((i % 16) * 20 + 10, (i // 16) * 32 + 25), ch, font=font, fill=(255, 255, 255, 255), anchor='ms')
    im.save(path('Textures', 'ascii.png'), optimize=True)


images()
sounds()
music()
if len(sys.argv) > 1:
    ascii_atlas(sys.argv[1])
