FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /service

COPY requirements.txt .
RUN pip install --no-cache-dir \
    fastapi==0.115.0 \
    'uvicorn[standard]==0.30.6' \
    httpx==0.27.2 \
    pydantic==2.9.2

COPY app ./app

RUN useradd --create-home --uid 1000 svc && chown -R svc:svc /service
USER svc

ENV PROVIDER=ollama \
    OLLAMA_BASE_URL=http://ollama:11434 \
    OLLAMA_MODEL=llama3.1:8b \
    MODERATION_VOTES=5 \
    LOG_LEVEL=INFO

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health',timeout=3).status==200 else 1)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
