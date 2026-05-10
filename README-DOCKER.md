# Fixauto Decarie Inventory — Docker Setup

Running with Docker means **no .NET installation needed** on any machine.
One command starts the app. Data survives restarts automatically.

---

## Requirements

- Windows 10/11 with **Docker Desktop** installed
- Download: https://www.docker.com/products/docker-desktop/
- After installing, make sure Docker Desktop is **running** (whale icon in taskbar)

---

## Start the app (first time)

1. Open **PowerShell** or **Command Prompt** in this folder:
   ```
   cd C:\FixautoInventory
   ```

2. Build and start:
   ```
   docker compose up -d
   ```
   First run downloads the .NET image (~200MB) and builds the app.
   This takes 2–5 minutes. Only once.

3. Open your browser:
   ```
   http://localhost:5000
   ```

4. Default login: **admin / admin123**

---

## Daily use

**Start the app:**
```
docker compose up -d
```

**Stop the app:**
```
docker compose down
```

**See logs (if something is wrong):**
```
docker compose logs
```

**Restart:**
```
docker compose restart
```

---

## LAN access (other devices on your Wi-Fi)

1. Find your PC's IP:
   ```
   ipconfig
   ```
   Look for **IPv4 Address** (e.g. 192.168.1.10)

2. On any phone or other computer, open:
   ```
   http://192.168.1.10:5000
   ```

3. Allow through Windows Firewall (first time only):
   ```
   netsh advfirewall firewall add rule name="Fixauto Inventory" dir=in action=allow protocol=TCP localport=5000
   ```

---

## Your data

All data is stored in the **`data/`** folder next to this file:
- `data/inventory.db` — your entire inventory database

All uploaded photos are in the **`uploads/`** folder.

**To back up:** copy both the `data/` and `uploads/` folders somewhere safe.

**To move to another computer:** copy the entire project folder including `data/` and `uploads/`.

---

## Update the app

When you get a new version of the code:
```
docker compose down
docker compose up -d --build
```

Your data is safe — it lives in `data/` and `uploads/` which are never touched by updates.

---

## Auto-start when Windows boots

In Docker Desktop → Settings → General → check **"Start Docker Desktop when you log in"**

Then the containers with `restart: unless-stopped` will start automatically with Windows.

