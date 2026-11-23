# Backups

Aquí **no** se guardan copias de la base de datos.

Hasta el 23/11/2026 el backup nocturno de la Raspberry subía a esta carpeta la
base de datos completa (historial de escucha, favoritos e identificadores de
los usuarios). El repositorio es público, así que esas copias se eliminaron de
todo el historial y la subida quedó desactivada.

Las copias se guardan ahora solo en la Raspberry (`~/emusic-backups`, últimas
14 noches; ver `~/emusic-db-backup.sh`). Si se quiere una copia fuera de la
Raspberry, debe ir a un repositorio **privado** o a otro almacenamiento privado.
