# OCR sidecar (ADR "Searchable PDF successor for TIFFs"): a thin HTTP wrapper over OCRmyPDF, which drives
# Tesseract to turn a (multi-page) TIFF into a searchable PDF (page image + invisible positioned text layer).
# OCRmyPDF is MPL-2.0, Tesseract Apache-2.0, FastAPI/uvicorn MIT — all used as-is, no bundling into the Api.
# Our base (#2325, ADR 1088): Tesseract, OCRmyPDF and the Python packages are installed in base/ocr, built weekly into our
# registry; this image asks no mirror and no package index. A new digest arrives by a PR the vulnerability scan checks.
FROM ghcr.io/hebelconsulting/alcifra-base-ocr@sha256:0aaa22c9d16df48b252f9694b7b51a03358bda68a814d9ddb2e43a52d982b06d
COPY app.py /app/app.py
COPY patchcode.py /app/patchcode.py
WORKDIR /app
EXPOSE 8080

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
