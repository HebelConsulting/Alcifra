# OCR sidecar (ADR "Searchable PDF successor for TIFFs"): a thin HTTP wrapper over OCRmyPDF, which drives
# Tesseract to turn a (multi-page) TIFF into a searchable PDF (page image + invisible positioned text layer).
# OCRmyPDF is MPL-2.0, Tesseract Apache-2.0, FastAPI/uvicorn MIT — all used as-is, no bundling into the Api.
# Our base (#2325, ADR 1088): Tesseract, OCRmyPDF and the Python packages are installed in base/ocr, built weekly into our
# registry; this image asks no mirror and no package index. A new digest arrives by a PR the vulnerability scan checks.
FROM ghcr.io/hebelconsulting/alcifra-base-ocr@sha256:44cc029574b0b317db44e678b7b296257660ff1299cf9628876c14a0be6cdd38
COPY app.py /app/app.py
COPY patchcode.py /app/patchcode.py
WORKDIR /app
EXPOSE 8080

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
