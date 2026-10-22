FROM python:3.11-slim-bookworm
WORKDIR /opt/jp-vision-training
# Official 2.7.1 cu128 wheels include Blackwell support. Host NVIDIA driver and
# NVIDIA Container Toolkit must still support the rented RTX 5080.
RUN python -m pip install --no-cache-dir torch==2.7.1 --index-url https://download.pytorch.org/whl/cu128
COPY requirements.txt .
RUN python -m pip install --no-cache-dir -r requirements.txt
COPY dataset.py train.py ./
ENV PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "train.py"]
