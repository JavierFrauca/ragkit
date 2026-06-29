# Arquitectura de RagKit

Diagramas del funcionamiento actual: el **pipeline de consulta** (con enrutado,
perfiles y guardarails), el **pipeline de ingesta** y la **vista de componentes**.

---

## 1) Pipeline de consulta (`AskAsync` / `AskStreamAsync` / `ChatSession`)

Una pregunta atraviesa cuatro decisiones encadenadas con **degradación elegante**:
enrutado → guardarail de entrada → recuperación + prompt → respuesta → guardarail de salida.

```mermaid
flowchart TD
    Q([Pregunta del usuario]) --> RESOLVE

    subgraph RESOLVE["1 · Resolver alcance — ResolveRouteAsync"]
        direction TB
        R0{¿domain o profile<br/>explícitos?}
        R0 -- "sí" --> RUSE[Usa lo indicado]
        R0 -- "no" --> R1{"¿Routing on Y<br/>(varios dominios<br/>o hay perfiles)?"}
        R1 -- "no" --> RFALL[Dominio único si lo hay<br/>· sin perfil]
        R1 -- "sí" --> RROUTE[["QueryRouter (tier-2)<br/>elige dominio + perfil(es)"]]
        RROUTE --> R2{confianza ≥<br/>RoutingThreshold?}
        R2 -- "sí" --> RACC[Dominio + perfiles<br/>+ labels fusionadas]
        R2 -- "no" --> RFALL
    end

    RUSE --> GIN
    RACC --> GIN
    RFALL --> GIN

    subgraph GIN["2 · Guardarail de ENTRADA — siempre activo"]
        direction TB
        G1{Deterministas:<br/>longitud · inyección}
        G1 -- "incumple" --> BLOCK1[/Bloqueado/]
        G1 -- "ok" --> G2[["Red de seguridad LLM (tier-2)<br/>+ reglas del usuario"]]
        G2 --> G3{allowed?}
        G3 -- "no" --> BLOCK1
    end

    G3 -- "sí" --> RETR
    BLOCK1 --> REJ([GuardrailRejectionMessage])

    subgraph RETR["3 · Recuperar + elegir prompt"]
        direction TB
        RT1[["RetrieveAsync<br/>vector + BM25 (RRF) + rerank → TopK"]]
        RT2["SelectPrompt (cadena):<br/>(dominio,perfil) → DomainPrompts<br/>→ OneShot/Chat → por defecto"]
    end

    RETR --> ANS[["4 · tier-1 redacta la respuesta<br/>(grounded + citas)"]]

    ANS --> GOUT
    subgraph GOUT["5 · Guardarail de SALIDA — solo si hay reglas de salida"]
        direction TB
        O1{¿reglas de<br/>salida?}
        O1 -- "no" --> OPASS[passthrough]
        O1 -- "sí" --> O2[["LLM (tier-2)"]]
        O2 --> O3{allowed?}
        O3 -- "no" --> BLOCK2[/Bloqueado/]
    end

    OPASS --> OUT([Respuesta + citas])
    O3 -- "sí" --> OUT
    BLOCK2 --> REJ

    note1["En streaming, si hay reglas de SALIDA<br/>se bufferiza, se valida y se emite de golpe"]
    GOUT -.-> note1
```

**Notas de coste/latencia por consulta:** el enrutado es 1 llamada tier-2 (solo si
procede y solo en el 1er turno de un chat); el guardarail de entrada es **siempre**
1 llamada tier-2 (salvo cortocircuito determinista); la respuesta es 1 llamada tier-1;
el guardarail de salida añade 1 tier-2 solo si defines reglas de salida.

---

## 2) Pipeline de ingesta (`IngestAsync` / `IngestFileAsync`)

```mermaid
flowchart TD
    DOC([Texto o fichero]) --> EXT[Extraer texto<br/>PDF/DOCX/TXT]
    EXT --> D0{¿hay dominios<br/>definidos?}
    D0 -- "no" --> REJ([Rechazado])
    D0 -- "sí" --> D1{¿dominio explícito?}

    D1 -- "no · AutoClassify" --> CLS[["Classifier (tier-2)<br/>dominio + etiquetas + confianza"]]
    CLS --> TH{confianza ≥<br/>ClassificationThreshold?}
    TH -- "no" --> REJ
    TH -- "sí" --> CHUNK

    D1 -- "sí" --> VAL{¿dominio válido?}
    VAL -- "no" --> REJ
    VAL -- "sí" --> CHUNK

    CHUNK[Trocear por frontera<br/>de frase] --> EMB[["Embedder<br/>(Local / ONNX / API·Ollama)"]]
    EMB --> STORE[["IVectorStore<br/>InMemory · Qdrant · Postgres · SQL Server"]]
    STORE --> LEX[Índice léxico BM25<br/>(para búsqueda híbrida)]
    STORE --> OK([Indexado:<br/>dominio, etiquetas, nº chunks])
```

---

## 3) Vista de componentes

La fachada `RagClient` orquesta; lo que cambia rápido (LLM, embeddings, store) vive
tras interfaces. Los **dos tiers** son clientes `IChatClient` compatibles OpenAI.

```mermaid
flowchart LR
    APP([Tu app]) --> RC

    subgraph RC["RagClient (fachada)"]
        direction TB
        ROUTER[QueryRouter]
        GUARD[Guardrail]
        CLASS[Classifier]
        RETRIEVE[RetrieveAsync<br/>+ RRF + rerank]
        SELP[SelectPrompt]
        AGENT[Agent loop<br/>+ tools internas/MCP]
    end

    ROUTER --> T2
    GUARD --> T2
    CLASS --> T2
    RETRIEVE --> EMB
    RETRIEVE --> VS
    AGENT --> T1
    RC --> T1

    T1[["tier-1 · Answer<br/>IChatClient (OpenAI-compat)"]]
    T2[["tier-2 · Classifier<br/>IChatClient (barato/rápido)"]]
    EMB[["IEmbedder<br/>Local · ONNX · API/Ollama"]]
    VS[["IVectorStore<br/>InMemory · Qdrant · Postgres · SQL Server"]]
    MCP[["Servidores MCP externos<br/>(RagKit.Mcp)"]]
    AGENT --> MCP

    CFG[/"RagOptions (init):<br/>Profiles · DomainPrompts · Guardrails<br/>EnableQueryRouting · thresholds · TopK"/] -.-> RC
```

---

### Leyenda de la cadena de resolución (degradación elegante)

| Decisión | Orden de resolución (cae al siguiente si no hay match) |
|---|---|
| **Dominio** | explícito → enrutado (tier-2) → único dominio → ninguno |
| **Perfil** | explícito → seleccionado (tier-2, multi si `MultiProfile`) → ninguno |
| **Prompt** | `(dominio,perfil)` → `DomainPrompts[dominio]` → `OneShotPrompt`/`ChatPrompt` → por defecto |
| **Guardarail entrada** | deterministas (siempre) → LLM red de seguridad (siempre) + reglas del usuario |
| **Guardarail salida** | reglas de salida del ámbito (solo si existen) → passthrough |
