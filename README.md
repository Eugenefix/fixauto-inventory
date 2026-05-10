# 🔧 Fixauto Decarie Inventory

Bodyshop parts inventory management system.
Web-based, runs on your local network, accessible from any device.

## Features
- Add / edit / delete parts with photos
- Part groups, brand overview
- Shopping cart with print
- English & French
- User login with history log
- Export to Excel / PDF

---

## Run with Docker (recommended)

### Requirements
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) installed and running

### Steps

1. Create a folder on your PC, e.g. `C:\FixautoInventory`

2. Inside that folder, create a file called `docker-compose.yml` with this content:

```yaml
version: '3.8'
services:
  inventory:
    image: ghcr.io/YOUR-USERNAME/fixauto-inventory:latest
    container_name: fixauto-inventory
    ports:
      - "5000:5000"
    volumes:
      - ./data:/app/data
      - ./uploads:/app/wwwroot/uploads
    restart: unless-stopped
    environment:
      - ASPNETCORE_URLS=http://+:5000
      - DB_PATH=/app/data/inventory.db
      - UPLOAD_DIR=/app/wwwroot/uploads
```

3. Open PowerShell in that folder and run:
```
docker compose up -d
```

4. Open your browser: **http://localhost:5000**

5. Default login: **admin / admin123** — change this after first login!

---

## LAN access (phones, tablets, other PCs)

1. Find your IP: run `ipconfig` → look for IPv4 Address
2. Open on any device on the same Wi-Fi: `http://192.168.1.XX:5000`
3. Allow firewall (once):
```
netsh advfirewall firewall add rule name="Fixauto Inventory" dir=in action=allow protocol=TCP localport=5000
```

---

## Your data
- Database: `data/inventory.db`
- Photos: `uploads/`

Back up by copying these two folders. They survive app updates.

---

## Update to latest version
```
docker compose pull
docker compose up -d
```

