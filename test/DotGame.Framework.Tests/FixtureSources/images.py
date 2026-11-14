# images.py <out folder>: the sources of the fixtures in TestData/Astc and TestData/Ktx2, procedural.
# 61 x 45, a size that isn't a multiple of any block size.
import sys, os
import numpy as np
from PIL import Image

OUT = sys.argv[1]; os.makedirs(OUT, exist_ok=True)
W, H = 61, 45
y, x = np.mgrid[0:H, 0:W].astype(float)


def save(name, rgba):
    Image.fromarray(np.clip(np.round(rgba), 0, 255).astype(np.uint8), 'RGBA').save(os.path.join(OUT, name))


def coverage(d):
    # Antialiased edge from a signed distance (negative inside)
    return np.clip(0.5 - d, 0, 1)


# Shape: a sprite with alpha. A rounded body with a soft drop shadow, eyes and a highlight, on a
# transparent background: opaque, edge and translucent pixels.
ss = 4
yy, xx = (np.mgrid[0:H * ss, 0:W * ss] + 0.5) / ss
body = np.hypot((xx - 30) / 22, (yy - 21) / 17) - 1
body_d = body * 17
shadow = np.exp(-(((xx - 32) / 20) ** 2 + ((yy - 40) / 3.5) ** 2))
eye_l = np.hypot(xx - 23, yy - 18) - 3.2
eye_r = np.hypot(xx - 37, yy - 18) - 3.2
glint = np.hypot(xx - 20, yy - 11) - 2.5
a_body = coverage(body_d * ss)
col = np.zeros((H * ss, W * ss, 4))
col[..., :3] = [40, 40, 60]; col[..., 3] = shadow * 110
shade = 0.75 + 0.25 * np.clip(-(yy - 21) / 17, -1, 1)
body_rgb = np.stack([90 * shade, 170 * shade, 230 * shade], -1)
for a, rgb in ((a_body, body_rgb), (coverage(eye_l * ss) * a_body, np.array([25, 25, 35])),
               (coverage(eye_r * ss) * a_body, np.array([25, 25, 35])), (coverage(glint * ss) * a_body * 0.7, np.array([255, 255, 255]))):
    a = a[..., None]
    out_a = a + col[..., 3:4] / 255 * (1 - a)
    col[..., :3] = np.where(out_a > 0, (rgb * a + col[..., :3] * col[..., 3:4] / 255 * (1 - a)) / np.maximum(out_a, 1e-9), col[..., :3])
    col[..., 3:4] = out_a * 255
shape = col.reshape(H, ss, W, ss, 4)
alpha = shape[..., 3].mean(axis=(1, 3))
rgb = (shape[..., :3] * shape[..., 3:4]).sum(axis=(1, 3)) / np.maximum(shape[..., 3:4].sum(axis=(1, 3)), 1e-9)
save('shape.png', np.dstack([rgb, alpha]))

# Flat: flat-colored art, opaque. Bands, a disc, a triangle and a checker, hard and antialiased edges.
img = np.zeros((H, W, 4)); img[..., 3] = 255
img[..., :3] = np.where((y < 15)[..., None], [240, 200, 60], np.where((y < 30)[..., None], [60, 160, 120], [200, 80, 90]))
disc = coverage(np.hypot(x + 0.5 - 18, y + 0.5 - 22) - 11)[..., None]
img[..., :3] = img[..., :3] * (1 - disc) + np.array([250, 250, 245]) * disc
tri = ((x > 30) & (y > 5) & (x - 30 < (y - 5) * 0.9))[..., None]
img[..., :3] = np.where(tri, [50, 70, 160], img[..., :3])
chk = ((x > 46) & (y > 30) & (((x // 3) + (y // 3)) % 2 == 0))[..., None]
img[..., :3] = np.where(chk, [20, 20, 20], img[..., :3])
save('flat.png', img)

# Painting: smooth, textured, many colors (octaves of value noise), opaque.
rng = np.random.default_rng(5)


def value_noise(scale):
    g = rng.random((H // scale + 3, W // scale + 3))
    gy, gx = y / scale, x / scale
    y0, x0 = gy.astype(int), gx.astype(int); fy, fx = gy - y0, gx - x0
    fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
    a, b, c, d = g[y0, x0], g[y0, x0 + 1], g[y0 + 1, x0], g[y0 + 1, x0 + 1]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


n = [sum(value_noise(s) / s ** 0.5 for s in (2, 4, 8, 16)) for _ in range(3)]
n = [(v - v.min()) / (v.max() - v.min()) for v in n]
paint = np.dstack([60 + 180 * n[0], 40 + 150 * (0.6 * n[1] + 0.4 * n[0]), 70 + 170 * n[2], np.full((H, W), 255.0)])
save('painting.png', paint)
print('ok')
