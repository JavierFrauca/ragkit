# RagKit.Dashboard

Panel de mantenimiento **opt-in** para [RagKit](https://www.nuget.org/packages/RagKit):
dominios, etiquetas, documentos, chunks paginados, guardarails, perfiles, prompts,
ingesta y un playground de preguntas — todo sobre la API pública de `RagClient`,
sin build step de frontend (assets embebidos en el paquete).

```csharp
builder.Services.AddSingleton(rag); // tu RagClient ya creado
// ...
app.MapRagDashboard(path: "/rag-admin");
```

## ⚠️ Seguridad

El dashboard **no incluye autenticación propia** (se mantiene mínimo, igual que
hace el panel web de Qdrant). `MapRagDashboard` devuelve un
`IEndpointConventionBuilder`, así que puedes colgar tu propio esquema de
autorización de ASP.NET Core:

```csharp
app.MapRagDashboard(path: "/rag-admin").RequireAuthorization("AdminOnly");
```

Eres responsable de:
- no exponerlo en una interfaz pública sin autenticación/reverse proxy propios, o
- vincularlo solo a localhost si es de uso local.

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
