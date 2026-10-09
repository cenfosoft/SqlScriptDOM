workspace "Agente Revisor Adversarial" "Diagrama de contexto C4 Nivel 1 para SqlScriptDOM" {

    model {
        dev = person "Desarrollador contribuyente" "Crea PRs con cambios a las gramáticas, normalmente asistido por LLMs"
        maint = person "Mantenedor / revisor humano" "Valida los PRs que el agente no puede aprobar"

        reviewer = softwareSystem "Agente Revisor Adversarial" "Reglas de LLM y workflows que revisan PRs con enfoque adversarial, calculan la métrica de riesgo y documentan la auditoría"

        github = softwareSystem "GitHub" "Aloja el fork de SqlScriptDOM, los PRs y ejecuta GitHub Actions" "Externo"
        llm = softwareSystem "Proveedor de LLM" "API de los modelos usados por los agentes constructor y adversarial" "Externo"
        static = softwareSystem "Herramientas de análisis estático" "Semgrep, linters y reportes de cobertura de pruebas" "Externo"

        dev -> github "Abre y actualiza PRs" "Git / HTTPS"
        maint -> github "Revisa los PRs marcados y hace el merge" "Web / HTTPS"
        github -> reviewer "Dispara la revisión" "GitHub Actions"
        reviewer -> github "Publica comentarios, etiquetas y auditoría del riesgo" "GitHub API"
        reviewer -> llm "Envía diff y reglas; recibe revisión y riesgo" "API HTTPS"
        reviewer -> static "Ejecuta análisis y obtiene resultados" "CLI en el pipeline"
        dev -> reviewer "Recibe la revisión automática de sus PRs"
        maint -> reviewer "Atiende los PRs que el agente escala a revisión humana"
    }

    views {
        systemContext reviewer "Contexto" {
            include *
        }

        styles {
            element "Person" {
                shape Person
                background #08427b
                color #ffffff
            }
            element "Software System" {
                background #1168bd
                color #ffffff
            }
            element "Externo" {
                background #999999
                color #ffffff
            }
        }
    }
}
