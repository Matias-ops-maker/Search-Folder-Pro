# Search Folder Pro

Buscador local para Windows. Solo revisa las carpetas que el usuario elige. No borra archivos.

## Primera versión

- Busca por nombre y muestra carpeta, tipo, tamaño y fecha.
- Permite elegir carpetas, iniciar el escaneo y cancelarlo.
- Guarda el índice en el equipo. No sube archivos a internet.
- No busca dentro del contenido de documentos ni elimina archivos.

## Pantallas

- **Buscar:** campo de búsqueda, filtros y lista ordenable de resultados.
- **Carpetas:** elegir qué carpetas revisar y ver el avance.
- **Ajustes:** exclusiones y ubicación del índice.
- **Diseño:** ventana compacta, clara, cómoda con teclado y acorde a Windows.

## Tecnología

- **C# y WPF**
- **SQLite**
- **.NET 10**

## Compilar

Con el SDK de .NET 10 instalado, ejecutar desde esta carpeta:

```powershell
dotnet build .\src\SearchFolderPro.Core\SearchFolderPro.Core.csproj
```