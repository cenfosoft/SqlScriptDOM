# Alcance del proyecto: Agente Revisor Adversarial para SqlScriptDOM

## 1. Contexto breve

SqlScriptDOM es una biblioteca de código abierto, mantenida por Microsoft, que expone un *parser* de la sintaxis de T-SQL para las distintas versiones de SQL Server. Gran parte de los cambios en el repositorio consiste en extender las gramáticas, y muchos de ellos los proponen equipos que no son expertos en la biblioteca. La revisión humana de esos Pull Requests (PRs) es manual y lenta, lo que crea un cuello de botella frente a la velocidad de la codificación asistida por IA.

Este proyecto **no modifica el parser ni las gramáticas**. Se trabaja sobre un *fork* del repositorio original y se agrega una capa de automatización alrededor del proceso de revisión de PRs.

- Repositorio original: https://github.com/microsoft/SqlScriptDOM
- Fork del grupo: https://github.com/cenfosoft/SqlScriptDOM
## 2. Objetivo

Reducir el *Lead Time* (tiempo de entrega) de integración de PRs mediante una revisión de código automatizada con enfoque adversarial, asistida por LLMs, que calcule una métrica de riesgo auditable y decida si el PR puede avanzar o debe pasar a revisión humana.

## 3. Qué incluye el alcance

El proyecto construirá los siguientes componentes dentro del fork:

| # | Componente | Descripción |
|---|------------|-------------|
| 1 | Reglas de LLM para construcción de código | Instrucciones que guían al agente que escribe cambios: arquitectura de la biblioteca, casos típicos de cambio de gramática, prácticas de implementación y cobertura de pruebas esperada. |
| 2 | Reglas de LLM para revisión adversarial | Instrucciones para que un agente revisor busque activamente fallas, regresiones y omisiones en el cambio propuesto. |
| 3 | Reglas de LLM para la métrica de riesgo | Criterios y rúbrica para que los revisores calculen un nivel de riesgo del PR, combinando la revisión del LLM con señales deterministas. |
| 4 | Reglas de LLM para la pista de auditoría | Formato en que se documenta, dentro del PR, cómo se calculó el riesgo y por qué se tomó la decisión. |
| 5 | Workflows de GitHub Actions | Orquestación del flujo: disparo por PR, ejecución de análisis, ciclos entre agentes, publicación de resultados y etiquetado. |
| 6 | Integración de señales deterministas | Resultados de pruebas automatizadas, cobertura, linters y análisis estático (por ejemplo, Semgrep) como entrada al cálculo del riesgo. |
| 7 | Control de ciclos | Límite estricto de iteraciones de revisión y corrección entre agentes. Al agotarse, el PR se marca automáticamente para revisión humana. |

### Flujo funcional a implementar

1. Se abre o actualiza un PR en el fork.
2. GitHub Actions dispara el flujo y ejecuta pruebas, linters y análisis estático.
3. El agente revisor adversarial analiza el cambio y propone observaciones.
4. Se calcula la métrica de riesgo y se documenta en el PR.
5. Si el riesgo es aceptable, el PR queda apto para integración automatizada.
6. Si no lo es, el agente constructor corrige y se repite el ciclo.
7. Si se agota el límite de ciclos, el PR se etiqueta para revisión humana.

## 4. Qué NO incluye el alcance

- Modificar el código fuente del parser, las gramáticas o las pruebas existentes de SqlScriptDOM.
- Enviar PRs, issues o cambios al repositorio de Microsoft.
- Reemplazar por completo la revisión humana: el mantenedor conserva la decisión final en los casos escalados.
- Entrenar o ajustar modelos de lenguaje; solo se utilizan modelos existentes mediante API.
- Soportar otras plataformas de CI distintas de GitHub Actions.
- Cubrir todo el repositorio. El piloto se limitará a PRs del fork que toquen cambios de gramática o pruebas relacionadas.

## 5. Alcance por entregable

| Entregable | Contenido de este proyecto |
|------------|----------------------------|
| Entregable 1 | Análisis del contexto, propuesta, calendario, fork funcional, este documento de alcance y diagrama C4 de contexto. **No se escribe código en esta etapa.** |
| Entregable 2 | Primeras versiones de reglas y workflows, flujo de desarrollo (issues, ramas, Actions, estrategia de tests), riesgos, costos y métricas. |
| Entregable final | Flujo completo funcionando, pruebas piloto con PRs reales del fork, métricas alcanzadas y lecciones aprendidas. |

## 6. Criterios de éxito

- El flujo se ejecuta de punta a punta sobre un PR de prueba sin intervención manual, salvo en los casos escalados.
- El límite de ciclos funciona: ningún PR entra en un bucle infinito.
- Cada PR evaluado queda con una pista de auditoría legible del cálculo del riesgo.
- El *Lead Time* medido con el agente es menor que la línea base sin el agente: `<valor objetivo a definir en el Entregable 2>`.
- La tasa de falsos positivos (PRs legítimos bloqueados sin motivo) se mantiene en un nivel aceptable: `<umbral a definir>`.

## 7. Supuestos y restricciones

**Supuestos**
- Todos los integrantes pueden compilar el proyecto y ejecutar las pruebas en su equipo.
- El fork permite habilitar GitHub Actions y guardar secretos para el acceso al LLM.
- Se dispone de acceso a un proveedor de LLM: `<modelo/proveedor a definir>`.

**Restricciones**
- Plazo y tamaño del equipo: tres integrantes, dentro del calendario del curso.
- Costo: el uso de la API de un LLM debe mantenerse dentro de un presupuesto acotado: `<monto/límite a definir>`.
- Las claves de API se guardan solo como secretos de Actions, nunca en el repositorio.

## 8. Riesgos principales del alcance

| Riesgo | Mitigación prevista |
|--------|---------------------|
| Falsos positivos que bloquean contribuciones legítimas | Calibrar prompts y umbrales con PRs reales; permitir escalar a humano. |
| Resultados no deterministas del LLM | Combinar con señales deterministas y registrar la pista de auditoría. |
| Costo o límites de uso de la API | Limitar ciclos y tamaño del diff analizado; monitorear el consumo. |
| Exposición de secretos en el pipeline | Usar secretos de Actions y restringir permisos de los workflows. |

## 9. Estructura prevista en el repositorio

```
docs/
  alcance.md
  architecture/
    workspace.dsl            # Diagrama C4 como código
    c4-context.png           # Imagen exportada
  ai-usage.md                # Herramientas y prompts de IA usados
.github/
  workflows/                 # Se agrega desde el Entregable 2
```

Las reglas de LLM (construcción, revisión, riesgo y auditoría) se ubicarán en una carpeta dedicada, cuyo nombre se decidirá al iniciar la implementación.
