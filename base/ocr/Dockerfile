# The OCR sidecar's base (#2325, ADR 1088): Tesseract, OCRmyPDF and the Python packages, installed HERE, on the weekly
# base-image build, so building the OCR image asks no distribution mirror and no package index. ocr/Dockerfile builds
# FROM this image, pinned by digest.
FROM python:3.14-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
        ocrmypdf \
        pngquant \
        qpdf \
        tesseract-ocr-deu \
        tesseract-ocr-fra \
        tesseract-ocr-ita \
        tesseract-ocr-enm \
        tesseract-ocr-spa \
        tesseract-ocr-spa-old \
        tesseract-ocr-por \
        tesseract-ocr-ron \
        tesseract-ocr-rus \
    && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir fastapi uvicorn python-multipart numpy pillow anyio
