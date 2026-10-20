# Search Folder Pro

Buscador local para Windows. Solo revisa las carpetas que el usuario elige. No borra archivos.

## Primera versión

- Busca por nombre y texto, y filtra por tipo, tamaño y fecha.
- Permite agregar o quitar carpetas, actualizar el índice y cancelar el escaneo.
- Detecta cambios mientras la aplicación está abierta.
- Guarda el índice en el equipo. No sube archivos a internet.
- Busca texto en archivos comunes de hasta 1 MB, como TXT, MD, CSV, LOG, JSON y código.
- No lee PDF o Word ni elimina archivos.

## Pantallas

- **Buscar:** campo de búsqueda, filtros y lista ordenable de resultados.
- **Carpetas:** elegir qué carpetas revisar y ver el avance.
- **Ajustes:** exclusiones y ubicación del índice.
- **Diseño:** ventana compacta, clara, cómoda con teclado y acorde a Windows.

## Tecnología

- **C# y WPF**
- **SQLite**, para guardar el índice en el equipo.
- **.NET 10**

## Compilar

Con el SDK de .NET 10 instalado, ejecutar desde esta carpeta:

```powershell
dotnet run --project .\src\SearchFolderPro.Desktop\SearchFolderPro.Desktop.csproj
```