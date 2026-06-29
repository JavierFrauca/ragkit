# RagKit.Postgres

Conector de almacén vectorial **PostgreSQL + pgvector** para
[RagKit](https://www.nuget.org/packages/RagKit). El core se mantiene sin dependencias
de BBDD; este paquete se enchufa con su `Enable()`.

```csharp
using RagKit.Postgres;

PostgresStore.Enable();
var opts = new RagOptions {
    Store = new StoreConfig {
        Kind = VectorStoreKind.Postgres,
        ConnectionString = "Host=localhost;Username=…;Password=…;Database=…"
    }
};
```

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
