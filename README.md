# MineBackup - Automata Minecraft Szerver Mentő

A MineBackup egy Python alapú eszköz, amely segítségével automatikusan készíthetsz biztonsági mentéseket Minecraft szervereidről és a hozzájuk tartozó MySQL adatbázisokról, majd ezeket feltöltheted a Google Drive-ra.

## Főbb jellemzők

- **Párhuzamos tömörítés:** Több szervermappa egyidejű tömörítése a gyorsabb mentés érdekében.
- **MySQL mentés:** Adatbázis sémák és adatok kimentése (INSERT chunking a memória kímélése érdekében).
- **Google Drive integráció:** Automatikus feltöltés közvetlenül a felhőbe.
- **Resumable Upload:** Nagy méretű fájlok (akár több GB) stabil feltöltése megszakadás esetén is.
- **Megtartási politika (Retention):** A beállított időnél régebbi mentések automatikus törlése a Google Drive-ról.
- **Kizárási minták:** Megadhatók mappák vagy fájlok (pl. `logs`, `cache`), amiket nem szeretnél a mentésbe foglalni.
- **Naplózás:** Részletes log fájlok készülnek minden futásról a `logs` mappába.

## Előfeltételek

- Python 3.11 vagy újabb
- Google Cloud Projekt (Drive API engedélyezve)
- `credentials.json` fájl a Google Cloud Console-ból

## Gyors telepítés

1. Klónozd vagy töltsd le a tárolót.
2. Telepítsd a szükséges függőségeket:
   ```bash
   pip install -r requirements.txt
   ```
3. Másold a `credentials.json` fájlt a gyökérkönyvtárba.
4. Állítsd be a `config.json` fájlt a saját elérési útjaidra és a Google Drive mappa azonosítódra.

A részletes beállítási útmutatót a [SETUP.md](SETUP.md) fájlban találod.

## Konfiguráció (config.json)

```json
{
  "backup_sources": [
    "C:\\Minecraft\\Survival_Server",
    "C:\\Minecraft\\Lobby_Server"
  ],
  "exclude_patterns": [
    "cache",
    "logs",
    "libraries"
  ],
  "mysql": {
    "enabled": true,
    "host": "localhost",
    "port": 3306,
    "user": "root",
    "password": "jelszo",
    "databases": [
      "minecraft_db"
    ]
  },
  "drive_folder_id": "GOOGLE_DRIVE_MAPPA_ID",
  "retention_days": 10,
  "temp_zip_folder": "temp"
}
```

## Használat

A mentés manuális indításához futtasd a fő szkriptet:

```bash
python backup.py
```

Az első futtatáskor a program megnyit egy böngészőt a Google hitelesítéshez. Ezután egy `token.json` fájl jön létre, így a további futtatások már teljesen automatikusan, beavatkozás nélkül történnek.

## Automatizálás

Windows alatt ajánlott a **Feladatütemező (Task Scheduler)** használata a napi mentésekhez. Linux alatt a **crontab** segítségével ütemezhető a futtatás.

---
Készítette: rb98t
