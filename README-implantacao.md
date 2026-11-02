# Preparação e implantação

Payments possui Dockerfile multi-stage e Compose local conectado à infraestrutura G1. Consulte [operação](docs/operations.md) para configurar, aplicar a migration explícita, executar e demonstrar os resultados; [README](README.md) para o estado técnico. Compose completo pertence ao G6 e manifests /k8s ao G7, usando Docker Desktop Kubernetes.

Não versionar .env, AGENTS.md, specs/, .codex/ ou docs/course/. Histórico Git independente; sem push ou publicação automática de imagens.
