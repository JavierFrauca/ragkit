# ADR-004: Integration Tests CI with Docker Services

**Date:** 2026-07-24
**Status:** Accepted

## Context

Los tests de integración contra Qdrant, Postgres y SQL Server se auto-omiten
en CI porque los servicios no están disponibles. Necesitamos una forma de
ejecutarlos en CI para detectar regresiones entre backends.

## Decision

1. **GitHub Actions service containers** (no docker compose): GitHub Actions
   soporta `services` nativos con health checks, lo que evita la sobrecarga
   de instalar docker compose en el runner.

2. **Job separado `integration-tests`**: Se ejecuta después de `build-test`
   para no penalizar el feedback loop rápido de las pruebas unitarias.

3. **docker-compose.yml para uso local**: Permite que los desarrolladores
   ejecuten `docker compose up -d --wait` antes de `dotnet test`.

4. **SQL Server 2022 como placeholder**: El tipo `VECTOR` nativo de SQL Server
   sólo está disponible en SQL Server 2025 (actualmente sin imagen Docker
   pública). Los tests de SQL Server se auto-omitirán hasta que la imagen
   esté disponible. Cambiar la etiqueta de imagen será suficiente cuando
   llegue el soporte.

## Services

| Servicio | Imagen | Puerto | Health Check |
|----------|--------|--------|-------------|
| Qdrant | `qdrant/qdrant:v1.13.4` | 6333 | `curl /healthz` |
| Postgres + pgvector | `pgvector/pgvector:pg17` | 5432 | `pg_isready` |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest` | 1433 | `sqlcmd SELECT 1` |

## Environment Variables

| Variable | Valor en CI |
|----------|------------|
| `RAGKIT_QDRANT_URL` | `http://127.0.0.1:6333` |
| `RAGKIT_POSTGRES_CONNECTION` | `Host=127.0.0.1;Port=5432;Database=ragkit;Username=ragkit;Password=ragkit` |
| `RAGKIT_SQLSERVER_CONNECTION` | `Server=127.0.0.1,1433;Database=ragkit;User Id=sa;Password=Ragkit123!;TrustServerCertificate=True` |

## Consequences

- Los tests de integración se ejecutan en CI en un job separado (~2 min extra).
- Los tests unitarios en `build-test` no se ven afectados (siguen siendo rápidos).
- Los desarrolladores pueden replicar el entorno de CI localmente con `docker compose`.
- Los tests de SQL Server se auto-omitirán hasta que SQL Server 2025 tenga imagen Docker.
