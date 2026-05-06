# MineBackup - .NET 10 Minecraft Biztonsági Mentő

A MineBackup egy modern, nagy teljesítményű biztonsági mentési megoldás Minecraft szerverekhez.
## ✨ Főbb Jellemzők

*   **Párhuzamosított Pipeline**: A tömörítés és a feltöltés szimultán történik minden forráshoz, kihasználva a többszálas CPU-k erejét.
*   **Google Drive Integráció**: Automatikus feltöltés a felhőbe, hitelesítés után.
*   **Félbehagyott mentések folytatása**: Ha a folyamat megszakad, a következő indításnál automatikusan felismeri és befejezi a temp mappában maradt mentések feltöltését.
*   **Adatbázis Mentés**: Natív MySQL/MariaDB dump támogatás (táblánkénti feldolgozás, alacsony RAM használat).
*   **Modern CLI**: Látványos, valós idejű folyamatjelzők (Spectre.Console), átviteli sebesség méréssel és hátralévő idő becsléssel.
*   **Automatikus Karbantartás**: A beállított megőrzési idő (Retention Days) alapján automatikusan törli a régi mentéseket a Drive-ról.
*   **Surgical Cleanup**: A helyi ideiglenes fájlok a sikeres feltöltés után azonnal törlődnek.

## 🛠️ Előfeltételek

*   **.NET 10 SDK** (vagy futtatókörnyezet)
*   Google Cloud projekt OAuth2 kliens azonosítóval (`credentials.json`)
*   MySQL/MariaDB hozzáférés (ha adatbázis mentés is szükséges)

## 🚀 Telepítés és Beállítás

1.  **Klónozd a repository-t.**
2.  **Google API beállítása**:
    *   Hozd létre a projektet a [Google Cloud Console](https://console.cloud.google.com/)-ban.
    *   Engedélyezd a **Google Drive API**-t.
    *   Hozz létre egy **OAuth client ID**-t (Desktop app), töltsd le a JSON-t, és mentsd el a projekt gyökerébe `credentials.json` néven.
3.  **Konfiguráció**:
    *   Másold le a `config.json` fájlt (vagy hozd létre).
    *   Állítsd be a `drive_folder_id`-t (a Google Drive mappa azonosítója a böngésző URL-jéből).
    *   Add meg a mentendő szerver mappákat a `backup_sources` listában.
4.  **Indítás**:
    ```bash
    dotnet run
    ```
    (Az első indításkor megnyílik a böngésző a hitelesítéshez).

## 📅 Ütemezés (Windows)

Javasolt a Windows Feladatütemező használata:
*   **Program/parancsfájl**: `dotnet`
*   **Argumentumok**: `run --project "C:\útvonal\a\MineBackup.csproj"`
*   **Kezdés helye**: Az alkalmazás mappája.

## 📄 Licenc

Ez a projekt saját használatra készült, de szabadon módosítható.
