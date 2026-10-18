"""Synthetic OCR test image, not user data."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[1]
out = root / 'TestResults' / 'ocr'
out.mkdir(parents=True, exist_ok=True)
font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 38)
image = Image.new('RGB', (1800, 480), 'white')
draw = ImageDraw.Draw(image)
rows = [('Product Code', 'Name', 'Quantity', 'Price'),
        ('001234', 'Coffee beans', '2', '1250.50'),
        ('009876', 'White rice', '3', '800.25'),
        ('AB-123', 'Green tea', '1', '450.00')]
for n, row in enumerate(rows):
    for x, value in zip((40, 460, 1180, 1470), row):
        draw.text((x, 40 + n*100), value, font=font, fill='black')
image.save(out / 'products.png')
print(out / 'products.png')
