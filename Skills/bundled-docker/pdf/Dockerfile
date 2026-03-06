# PDF Skill Dockerfile
FROM python:3.12-slim

# Install system dependencies for PDF processing and rendering
RUN apt-get update && apt-get install -y \
    poppler-utils \
    && rm -rf /var/lib/apt/lists/*

# Install Python dependencies for PDF manipulation
RUN pip install --no-cache-dir \
    reportlab \
    pdfplumber \
    pypdf \
    pypdf2

# Default command
CMD ["/bin/bash"]
