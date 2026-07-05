# RagCompleto — el tour completo de RagKit

El ejemplo que enseña casi todo lo que RagKit sabe hacer, en una app Blazor
real de varias páginas. Para el arranque mínimo, mira
[`RagSimple`](../RagSimple) o [`MiniRag`](../MiniRag).

## Qué trae
- **3 dominios con auto-clasificación real**: `fiscal`, `rrhh`, `legal`. Al
  ingerir sin fijar dominio, el tier-2 decide dónde encaja (o rechaza el
  documento si no llega al umbral de confianza).
- **Perfiles ("lentes") por dominio**: `asesor` (fiscal), `gestor` (rrhh),
  `abogado` (legal) — el tier-2 los selecciona al enrutar la pregunta.
- **Guardarails**: una regla de entrada, una de salida, y el check
  determinista de PII (`GuardrailPiiCheck`) activado.
- **Chat multi-turno con memoria real** (`RagClient.StartChat`, no historial
  manual) — y un **modo Agente** alternable (`AskAgentAsync` con
  `AgentToolScope.SearchOnly`: el modelo decide si busca, una tool por
  pregunta, sin memoria entre turnos porque hoy no hay una sesión agéntica).
- **Página de ingesta separada** (`/ingesta`): texto o fichero (PDF/DOCX/TXT),
  dominio explícito o auto-clasificado.
- **`RagKit.Dashboard` montado en `/rag-admin`**: gestión de dominios,
  etiquetas, documentos, chunks, perfiles, guardarails, prompts, ingesta con
  progreso y un playground — sin reimplementar nada de eso en esta app. Así
  se ve el patrón real: tu UI de consumidor (Chat + Ingesta) más el panel de
  administración del paquete, montados juntos.

## Cómo ejecutarlo
```bash
# 1) Instala Ollama y descarga los modelos (una sola vez; los mismos que MiniRag/RagSimple)
ollama pull qwen2.5:7b
ollama pull nomic-embed-text

# 2) Arranca el ejemplo
dotnet run --project examples/RagCompleto
# abre http://localhost:5119        (chat + ingesta)
# abre http://localhost:5119/rag-admin/   (panel de administración)
```
LLM/embedder se configuran en `appsettings.json` (sección `"Rag"`), igual que
en `RagSimple` — apuntar a un proveedor en la nube es editar el fichero.

## Qué verás
- **Chat** (`/`): elige un dominio o deja que se enrute solo; alterna entre
  modo RAG (con memoria) y modo Agente; la respuesta llega en streaming (modo
  RAG) con citas, y muestra a qué dominio/perfil se enrutó.
- **Ingesta** (`/ingesta`): pega texto o sube un fichero; si dejas el dominio
  en "auto-clasificar", verás la confianza y las etiquetas que el tier-2 le
  asignó (o el motivo del rechazo si no encajó en ningún dominio).
- **Admin** (`/rag-admin`): el panel de mantenimiento de `RagKit.Dashboard` —
  sin autenticación propia (ver su README de seguridad); en un despliegue
  real, encadena tu propio esquema de auth sobre `MapRagDashboard(...)`.
