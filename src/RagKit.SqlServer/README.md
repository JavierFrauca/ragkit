# RagKit.SqlServer

Conector de almacén vectorial **SQL Server 2025** (tipo nativo `VECTOR`) para
[RagKit](https://www.nuget.org/packages/RagKit). El core se mantiene sin dependencias
de BBDD; este paquete se enchufa con su `Enable()`.

```csharp
using RagKit.SqlServer;

SqlServerStore.Enable();
var opts = new RagOptions {
    Store = new StoreConfig {
        Kind = VectorStoreKind.SqlServer,
        ConnectionString = "Server=…;Database=…;User Id=…;Password=…;TrustServerCertificate=True"
    }
};
```

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
