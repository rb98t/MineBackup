import os
import json
import zipfile
import logging
from datetime import datetime, timedelta
from concurrent.futures import ThreadPoolExecutor, as_completed
import threading
from tqdm import tqdm
import shutil
import time
from pathlib import Path
from typing import List, Dict, Any, Optional, Tuple

try:
    import pymysql
    import pymysql.converters
    import pymysql.cursors
except ImportError:
    pass

# tqdm szálbiztos beállítása
tqdm.set_lock(threading.RLock())

# Google API csomagok
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import InstalledAppFlow
from google.auth.transport.requests import Request
from googleapiclient.discovery import build
from googleapiclient.http import MediaFileUpload

# Engedélyek a Google Drive fájlok kezeléséhez
SCOPES = ['https://www.googleapis.com/auth/drive.file']

def setup_logger() -> None:
    script_dir = Path(__file__).parent.resolve()
    logs_dir = script_dir / 'logs'
    logs_dir.mkdir(exist_ok=True)
    
    timestamp = datetime.now().strftime('%Y-%m-%d_%H-%M-%S')
    log_file = logs_dir / f'backup_{timestamp}.log'
    
    logging.basicConfig(
        filename=str(log_file),
        level=logging.INFO,
        format='%(asctime)s [%(levelname)s] %(message)s',
        datefmt='%Y-%m-%d %H:%M:%S'
    )

def load_config() -> Optional[Dict[str, Any]]:
    script_dir = Path(__file__).parent.resolve()
    config_path = script_dir / 'config.json'
    try:
        with open(config_path, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception as e:
        logging.error(f"Nem sikerult betölteni a config.json fajlt: {e}", exc_info=True)
        tqdm.write(f"Hiba a config.json betöltésekor: {e}")
        return None

def should_exclude(file_path: Path, exclude_patterns: List[str]) -> bool:
    path_parts = file_path.parts
    for pattern in exclude_patterns:
        if pattern in path_parts:
            return True
    return False

def zip_server(source_dir: str, dest_dir: str, exclude_patterns: List[str], pbar: tqdm) -> Optional[str]:
    source_path = Path(source_dir).resolve()
    dest_path = Path(dest_dir).resolve()
    server_name = source_path.name
    timestamp = datetime.now().strftime('%Y-%m-%d_%H-%M-%S')
    zip_filename = f"{server_name}_{timestamp}.zip"
    zip_filepath = dest_path / zip_filename
    
    logging.info(f"[{server_name}] Tömörítés megkezdése: {zip_filepath}")
    
    try:
        dest_path.mkdir(parents=True, exist_ok=True)
        
        # 1. Fájlok összeszámolása a progress barhoz
        total_files = 0
        for root, dirs, files in os.walk(source_path):
            try:
                root_path = Path(root)
                dirs[:] = [d for d in dirs if not should_exclude(root_path / d, exclude_patterns)]
                for file in files:
                    if not should_exclude(root_path / file, exclude_patterns):
                        total_files += 1
            except (PermissionError, OSError):
                continue

        pbar.total = total_files
        pbar.refresh()

        # 2. Tömörítés és a progress bar frissítése
        with zipfile.ZipFile(zip_filepath, 'w', zipfile.ZIP_DEFLATED, allowZip64=True) as zipf:
            for root, dirs, files in os.walk(source_path):
                try:
                    root_path = Path(root)
                    dirs[:] = [d for d in dirs if not should_exclude(root_path / d, exclude_patterns)]
                    
                    for file in files:
                        file_full_path = root_path / file
                        if not should_exclude(file_full_path, exclude_patterns):
                            arcname = file_full_path.relative_to(source_path)
                            arcname = Path(server_name) / arcname
                            try:
                                zipf.write(file_full_path, arcname)
                            except (PermissionError, OSError) as pe:
                                logging.warning(f"[{server_name}] Fájl kihagyva (hozzáférés megtagadva): {file_full_path} - {pe}")
                            finally:
                                pbar.update(1)
                except (PermissionError, OSError) as e:
                    logging.warning(f"[{server_name}] Mappa kihagyva (hozzáférés megtagadva): {root} - {e}")
                    continue
                        
        logging.info(f"[{server_name}] Tömörítés sikeresen befejezve.")
        return str(zip_filepath)
    except Exception as e:
        logging.error(f"[{server_name}] Hiba a tömörítés során: {e}", exc_info=True)
        if zip_filepath.exists():
            zip_filepath.unlink()
        return None

def get_drive_service() -> Optional[Any]:
    script_dir = Path(__file__).parent.resolve()
    creds = None
    token_path = script_dir / 'token.json'
    credentials_path = script_dir / 'credentials.json'

    if token_path.exists():
        creds = Credentials.from_authorized_user_file(str(token_path), SCOPES)
    
    if not creds or not creds.valid:
        if creds and creds.expired and creds.refresh_token:
            try:
                creds.refresh(Request())
            except Exception as e:
                logging.error(f"Token frissítési hiba: {e}", exc_info=True)
                creds = None
                
        if not creds:
            if not credentials_path.exists():
                logging.error(f"Hiányzik a credentials.json a {script_dir} mappabol. A Google Drive hitelesítés nem lehetséges.")
                return None
            flow = InstalledAppFlow.from_client_secrets_file(str(credentials_path), SCOPES)
            creds = flow.run_local_server(port=0)
            with open(token_path, 'w') as token:
                token.write(creds.to_json())

    try:
        service = build('drive', 'v3', credentials=creds)
        return service
    except Exception as e:
        logging.error(f"Hiba a Drive szolgáltatás létrehozásakor: {e}", exc_info=True)
        return None

def dump_database(db_config: Dict[str, Any], dest_dir: str, pbar: tqdm) -> Optional[str]:
    db_name = db_config['name']
    host = db_config.get('host', 'localhost')
    port = db_config.get('port', 3306)
    user = db_config.get('user', 'root')
    password = db_config.get('password', '')
    
    dest_path = Path(dest_dir).resolve()
    timestamp = datetime.now().strftime('%Y-%m-%d_%H-%M-%S')
    zip_filename = f"DB_{db_name}_{timestamp}.zip"
    zip_filepath = dest_path / zip_filename
    folder_path = dest_path / f"DB_{db_name}_{timestamp}"
    
    logging.info(f"[{db_name}] Adatbázis mentés megkezdése...")
    
    try:
        # SSDictCursor is used to avoid Out Of Memory errors on large databases
        conn = pymysql.connect(
            host=host, port=port, user=user, password=password, database=db_name, 
            cursorclass=pymysql.cursors.SSDictCursor
        )
        
        folder_path.mkdir(parents=True, exist_ok=True)
        schema_file = folder_path / f"{db_name}_schema.sql"
        data_file = folder_path / f"{db_name}_data.sql"
        
        # Sima kurzorral lekerjük a tablakat a pbar celtudatossaga vegett
        tables = []
        with pymysql.connect(host=host, port=port, user=user, password=password, database=db_name, cursorclass=pymysql.cursors.DictCursor) as dict_conn:
            with dict_conn.cursor() as dict_cursor:
                dict_cursor.execute("SHOW TABLES")
                tables = [list(row.values())[0] for row in dict_cursor.fetchall()]
        
        pbar.total = len(tables) * 2
        pbar.refresh()
            
        with conn.cursor() as cursor:
            # 1. Schema kimentese
            with open(schema_file, 'w', encoding='utf-8', errors='replace') as sf:
                sf.write("SET FOREIGN_KEY_CHECKS=0;\n\n")
                for table in tables:
                    cursor.execute(f"SHOW CREATE TABLE `{table}`")
                    row = cursor.fetchone()
                    if row:
                        create_stmt = list(row.values())[1]
                        sf.write(f"DROP TABLE IF EXISTS `{table}`;\n")
                        sf.write(f"{create_stmt};\n\n")
                    pbar.update(1)
                sf.write("SET FOREIGN_KEY_CHECKS=1;\n")
            
            # 2. Adatok kimentese (Chunkokban olvasva, RAM kimeles cimen)
            with open(data_file, 'w', encoding='utf-8', errors='replace') as df:
                df.write("SET FOREIGN_KEY_CHECKS=0;\n\n")
                for table in tables:
                    cursor.execute(f"SELECT * FROM `{table}`")
                    
                    chunk_size = 500
                    cols = None
                    cols_str = ""
                    
                    while True:
                        rows_chunk = cursor.fetchmany(chunk_size)
                        if not rows_chunk:
                            break
                            
                        if cols is None:
                            cols = list(rows_chunk[0].keys())
                            cols_str = ", ".join([f"`{c}`" for c in cols])
                            
                        values_list = []
                        for row in rows_chunk:
                            vals = []
                            for val in row.values():
                                if val is None:
                                    vals.append("NULL")
                                elif isinstance(val, (int, float)):
                                    vals.append(str(val))
                                elif isinstance(val, bytes):
                                    escaped = pymysql.converters.escape_bytes(val)
                                    vals.append(f"_binary '{escaped}'")
                                elif isinstance(val, datetime):
                                    vals.append(f"'{val.strftime('%Y-%m-%d %H:%M:%S')}'")
                                else:
                                    escaped = pymysql.converters.escape_string(str(val))
                                    vals.append(f"'{escaped}'")
                            values_list.append("(" + ", ".join(vals) + ")")
                        
                        df.write(f"INSERT INTO `{table}` ({cols_str}) VALUES\n")
                        df.write(",\n".join(values_list) + ";\n\n")
                    
                    pbar.update(1)
                
                # Biztosítjuk, hogy ne maradjon olvasatlan adat
                try:
                    while cursor.nextset():
                        pass
                except:
                    pass

                df.write("SET FOREIGN_KEY_CHECKS=1;\n")
                
        conn.close()
        
        # ZIP letrehozasa
        with zipfile.ZipFile(zip_filepath, 'w', zipfile.ZIP_DEFLATED) as zipf:
            zipf.write(schema_file, f"{db_name}_schema.sql")
            zipf.write(data_file, f"{db_name}_data.sql")
            
        shutil.rmtree(folder_path)
        logging.info(f"[{db_name}] Mentes sikeresen befejezve.")
        return str(zip_filepath)
    except Exception as e:
        logging.error(f"[{db_name}] Hiba az adatbazis mentes soran: {e}", exc_info=True)
        if folder_path.exists():
            shutil.rmtree(folder_path, ignore_errors=True)
        if zip_filepath.exists():
            zip_filepath.unlink()
        return None

def upload_file(service: Any, filepath: str, folder_id: str) -> bool:
    file_path = Path(filepath)
    filename = file_path.name
    file_metadata = {
        'name': filename,
        'parents': [folder_id]
    }
    
    file_size = file_path.stat().st_size
    logging.info(f"Feltöltés indítása: {filename} ({file_size} bytes)")
    
    try:
        media = MediaFileUpload(str(file_path), mimetype='application/zip', resumable=True, chunksize=50*1024*1024)
        request = service.files().create(body=file_metadata, media_body=media, fields='id')
        
        response = None
        previous_progress = 0
        
        with tqdm(total=file_size, unit='B', unit_scale=True, unit_divisor=1024, 
                  desc=f"Feltöltés: {filename[:20]:<20}", leave=True, 
                  ascii=" #", bar_format="{desc}: {percentage:3.0f}%|{bar}| {n_fmt}/{total_fmt} [{elapsed}<{remaining}, {rate_fmt}]") as pbar:
            while response is None:
                status, response = request.next_chunk()
                if status:
                    current_progress = int(status.resumable_progress)
                    pbar.update(current_progress - previous_progress)
                    previous_progress = current_progress
            
            pbar.update(file_size - previous_progress)
                
        logging.info(f"Feltöltés befejezve: {filename}. Fájl ID: {response.get('id')}")
        return True
    except Exception as e:
        logging.error(f"feltöltés sikertelen a(z) {filename} fájlnál: {e}", exc_info=True)
        tqdm.write(f"Hiba a feltöltésnél: {filename} - {e}")
        return False

def purge_old_backups(service: Any, folder_id: str, retention_days: int) -> None:
    logging.info(f"keresés a {retention_days} napnál régebbi mentések törléséhez...")
    try:
        cutoff_date = datetime.utcnow() - timedelta(days=retention_days)
        cutoff_str = cutoff_date.isoformat("T") + "Z"
        
        query = f"'{folder_id}' in parents and modifiedTime < '{cutoff_str}' and trashed=false"
        
        results = service.files().list(q=query, spaces='drive', fields="nextPageToken, files(id, name, modifiedTime)").execute()
        items = results.get('files', [])
        
        if not items:
            logging.info("Nem található olyan régi mentés, amit törölni kellene.")
            tqdm.write("Nincs törlendő régi mentés a Drive-on.")
        else:
            for item in items:
                tqdm.write(f"Régi mentés törlése a Drive-ról: {item['name']}")
                logging.info(f"Régi mentés törlése: {item['name']} (Utolsó módosítás: {item['modifiedTime']})")
                service.files().delete(fileId=item['id']).execute()
                logging.info(f"Sikeresen törölve a Drive-rol: {item['name']}")
    except Exception as e:
        logging.error(f"Hiba a régi mentések törlése során: {e}", exc_info=True)
        tqdm.write(f"Hiba a Drive karbantartásakor: {e}")

def main() -> None:
    setup_logger()
    
    print("="*50)
    print("Minecraft Biztonsági Mentés".center(50))
    print("="*50)
    logging.info("=== Biztonsági mentés folyamat elindult ===")
    
    config = load_config()
    if not config:
        return
            
    sources = config.get('backup_sources', [])
    exclude_patterns = config.get('exclude_patterns', [])
    temp_folder = config.get('temp_zip_folder', 'temp')
    drive_folder_id = config.get('drive_folder_id')
    retention_days = config.get('retention_days', 10)
    
    if drive_folder_id == "IDE_MASOLD_BE_A_DRIVE_MAPPA_ID-T":
        print("[HIBA] Kérlek állítsd be a 'drive_folder_id'-t a config.json fájlban!")
        logging.error("A 'drive_folder_id' nincs beállítva a config.json-ben.")
        return

    temp_path = Path(temp_folder).resolve()

    # Félbehagyott mentések feltöltése és Temp mappa takarítása induláskor
    if temp_path.exists():
        leftover_zips = [f for f in temp_path.iterdir() if f.suffix == '.zip' and f.is_file()]
        
        if leftover_zips:
            print(f"[0/3] {len(leftover_zips)} db félbehagyott mentés feltöltése...")
            service = get_drive_service()
            if service:
                for file_path in leftover_zips:
                    success = upload_file(service, str(file_path), drive_folder_id)
                    if success:
                        try:
                            file_path.unlink()
                            logging.info(f"Félbehagyott ideiglenes zip fájl törölve: {file_path}")
                        except Exception as e:
                            logging.error(f"Hiba a helyi zip fájl törlésekor {file_path}: {e}", exc_info=True)
            else:
                print("Nem sikerült a hitelesítés, a félbehagyott mentések feltöltése elmaradt.")

        try:
            # A maradék fájlok takarítása
            for item_path in temp_path.iterdir():
                try:
                    if item_path.is_file() or item_path.is_symlink():
                        item_path.unlink()
                    elif item_path.is_dir():
                        shutil.rmtree(item_path)
                except Exception as e:
                    logging.error(f"Nem sikerült törölni: {item_path}. Ok: {e}", exc_info=True)
        except Exception as e:
            logging.error(f"Hiba a temp mappa takarításakor: {e}", exc_info=True)

    # 1. Lépés: Párhuzamos tömörítés
    print("[1/3] Mentések előkészítése (Szerverek és Adatbázisok)...")
    ready_files = []
    
    mysql_config = config.get('mysql', {})
    mysql_enabled = mysql_config.get('enabled', False)
    mysql_dbs = mysql_config.get('databases', [])
    
    tasks: List[Tuple[str, str]] = []
    for source in sources:
        tasks.append(('server', source))
        
    if mysql_enabled and mysql_dbs:
        for db in mysql_dbs:
            tasks.append(('mysql', db))
    
    pbars = []
    for i, task in enumerate(tasks):
        task_type, item = task
        if task_type == 'server':
            name = Path(item).name
            desc = f"Szerver: {name[:14]:<14}"
            unit = ' fájl'
        else:
            desc = f"Adatbázis: {item[:12]:<12}"
            unit = ' tábla'
            
        pbars.append(tqdm(total=0, desc=desc, position=i, leave=True, unit=unit, ascii=" #", bar_format="{desc}: {percentage:3.0f}%|{bar}| {n_fmt}/{total_fmt} [{elapsed}<{remaining}, {rate_fmt}]"))
    
    with ThreadPoolExecutor(max_workers=max(1, len(tasks))) as executor:
        futures = {}
        for i, task in enumerate(tasks):
            task_type, item = task
            if task_type == 'server':
                future = executor.submit(zip_server, item, temp_folder, exclude_patterns, pbars[i])
            else:
                db_conf = mysql_config.copy()
                db_conf['name'] = item
                future = executor.submit(dump_database, db_conf, temp_folder, pbars[i])
            futures[future] = item
            
        for future in as_completed(futures):
            item = futures[future]
            try:
                result_filepath = future.result()
                if result_filepath:
                    ready_files.append(result_filepath)
            except Exception as exc:
                logging.error(f"{item} mentese kozben kivetel tortent: {exc}", exc_info=True)
                tqdm.write(f"Hiba {Path(item).name} mentésekor: {exc}")
                
    for pbar in pbars:
        pbar.close()
                
    print("") # Csak egyetlen üres sor a progress barok után
                
    if not ready_files:
        print("Egyetlen adatot sem sikerült kimenteni. Megszakítás.")
        return
        
    # 2. Lépés: Hitelesítés és feltöltés
    print("[2/3] Csatlakozás a Google Drive-hoz...")
    # Először egy alap hitelesítés, hogy meglegyen a token
    if not get_drive_service():
        print("Nem sikerült a hitelesítés a Google Drive-al. Megszakítás.")
        return
        
    print("[3/3] Fájlok feltöltése a Drive-ra (párhuzamosan)...")
    
    def upload_worker(filepath: str, pos: int) -> bool:
        # Minden szál saját service-t kap a szálbiztosság miatt
        thread_service = get_drive_service()
        if not thread_service:
            return False
            
        file_path = Path(filepath)
        filename = file_path.name
        file_metadata = {'name': filename, 'parents': [drive_folder_id]}
        file_size = file_path.stat().st_size
        
        try:
            # 100 MB-os szeletek a még kevesebb várakozás érdekében
            media = MediaFileUpload(str(file_path), mimetype='application/zip', resumable=True, chunksize=100*1024*1024)
            request = thread_service.files().create(body=file_metadata, media_body=media, fields='id')
            
            response = None
            previous_progress = 0
            
            with tqdm(total=file_size, unit='B', unit_scale=True, unit_divisor=1024, 
                      desc=f"Feltöltés: {filename[:20]:<20}", leave=True, position=pos, 
                      ascii=" #", bar_format="{desc}: {percentage:3.0f}%|{bar}| {n_fmt}/{total_fmt} [{elapsed}<{remaining}, {rate_fmt}]") as pbar:
                while response is None:
                    status, response = request.next_chunk()
                    if status:
                        current_progress = int(status.resumable_progress)
                        pbar.update(current_progress - previous_progress)
                        previous_progress = current_progress
                pbar.update(file_size - previous_progress)
            
            logging.info(f"Feltöltés befejezve: {filename}")
            file_path.unlink() 
            return True
        except Exception as e:
            logging.error(f"Hiba a feltöltésnél ({filename}): {e}", exc_info=True)
            return False

    # Max 10 párhuzamos feltöltés a maximális sávszélesség kihasználásához
    max_upload_threads = min(10, len(ready_files))
    with ThreadPoolExecutor(max_workers=max_upload_threads) as executor:
        upload_futures = []
        for i, filepath in enumerate(ready_files):
            upload_futures.append(executor.submit(upload_worker, filepath, i))
            
        for future in as_completed(upload_futures):
            future.result()

    print("\n" * max_upload_threads) # Hely hagyása a progress barok után
                
    # 3. Lépés: Régi mentések karbantartása
    print("Karbantartás (Régi mentések ellenőrzése)...")
    service = get_drive_service()
    if service and retention_days > 0:
        purge_old_backups(service, drive_folder_id, retention_days)
        
    print("="*50)
    print("Biztonsági mentés sikeresen befejeződött!".center(50))
    print("="*50)
    logging.info("=== Biztonsagi mentés folyamat sikeresen befejezodott ===")

if __name__ == "__main__":
    main()