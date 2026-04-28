# MineBackup - Beállítási Útmutató

## 1. Python Telepítése
1. Töltsd le a legújabb Pythont (pl. 3.11 vagy újabb) a [python.org](https://www.python.org/downloads/) oldalról.
2. **Kritikus lépés:** A telepítő indításakor az első ablakban pipáld be az **"Add Python to PATH"** (vagy "Add python.exe to PATH") opciót!
3. Telepítsd fel.

## 2. Szükséges Csomagok Telepítése
Nyiss egy parancssort (CMD vagy PowerShell) rendszergazdaként, és futtasd le ezt a parancsot a mappában:
```cmd
pip install -r requirements.txt
```

## 3. Google Drive API Beállítása
1. Látogass el a [Google Cloud Console](https://console.cloud.google.com/) oldalra.
2. Hozz létre egy új projektet (pl. "MineBackup").
3. Keresd meg a **Google Drive API**-t és engedélyezd (Enable).
4. Menj az "Credentials" (Hitelesítő adatok) menüpontba.
5. Hozz létre egy "OAuth client ID" típusú azonosítót (Alkalmazás típusa: Desktop app / Asztali alkalmazás).
6. Töltsd le a JSON fájlt, nevezd át **`credentials.json`**-re, és másold be a mappába.

## 4. Konfiguráció (`config.json`)
1. Hozz létre egy új mappát a Google Drive-odon, ahova a mentéseket szeretnéd.
2. Nyisd meg a mappát a böngészőben, és másold ki az azonosítóját a linkből (pl. `https://drive.google.com/drive/folders/EZ_AZ_AZONOSITO`).
3. Nyisd meg a `config.json` fájlt, és a `"YOUR_GOOGLE_DRIVE_FOLDER_ID"` értéket írd át erre az azonosítóra.
4. Ha szükséges, módosíthatod a `"retention_days"` értékét (jelenleg 10 nap).

## 5. Első Indítás (Azonosítás)
Nyiss egy parancssort ebben a mappában, és futtasd:
```cmd
python backup.py
```
Az első induláskor meg fog nyílni egy böngésző ablak, ahol be kell jelentkezned a Google fiókodba, és engedélyezned kell az alkalmazást. Ezt csak egyszer kell megtenni, a hitelesítés elmentődik a `token.json` fájlba.

## 6. Windows Feladatütemező (Task Scheduler) beállítása
1. Nyisd meg a Windows Start menüt, és keress rá a **Feladatütemező** (Task Scheduler) kifejezésre.
2. Kattints az "Alapfeladat létrehozása..." (Create Basic Task) gombra.
3. Adj neki nevet (pl. "Minecraft Mentés").
4. Állítsd be az időzítést (pl. Naponta, hajnali 4:00).
5. A Műveletnél válaszd a "Program indítása" opciót.
6. A "Program/parancsfájl" mezőbe írd be: `python` (vagy a python.exe teljes útvonalát, pl. `C:\Program Files\Python312\python.exe`).
7. Az "Argumentumok hozzáadása" mezőbe írd be: `backup.py`
8. A "Kezdés helye" (Start in) mezőbe írd be az alkalmazás elérési útját.
9. Mentsd el.

Kész vagy! A rendszer innentől automatikusan fut, ment, feltölt és törli a régi mentéseket.