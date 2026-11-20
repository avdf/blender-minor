# blender-minor

**BMLS: Blender Minor LLM Shortcuts.** Um servidor MCP pequeno (.NET) com atalhos seguros para o Blender, feito para
**modelos de linguagem pequenos** (de uns 0,5B a 4B parâmetros, mesmo em CPU antiga).

> English: see [README.md](README.md).

O modelo **não escreve Python**. Ele escolhe uma ferramenta pelo nome e preenche alguns campos simples:

```
create_sphere(radius=2, x=3, y=-5, color="blue")
move_object(name="Sphere", z=5, relative=true)
bevel_object(name="Cube", width=0.2, segments=3)
```

> **Status: experimental.** Testado com Blender 5.3 em Linux. Leia as [notas de segurança](#segurança) antes de
> expor o serviço numa rede.

## Por que existe

Modelos pequenos são ruins para escrever código `bpy`. Nos nossos testes eles erravam sempre as mesmas coisas:

- indentação errada (`IndentationError`);
- inventavam funções que não existem (`primitive_sphere_add`, `CREATE_SOLID`);
- copiavam o exemplo ao pé da letra e erravam quando precisavam adaptar;
- um deles (um 4B) escreveu código que apagava todos os objetos da cena.

E "pensar" é caro em CPU lenta (de 1 a 3 tokens por segundo): cada decisão que o modelo precisa tomar custa tempo.

## Ferramentas

Todos os campos numéricos são opcionais, salvo indicação. A cor aceita um nome (inglês ou português, inclusive no
feminino, como `roxa`) ou `#RRGGBB`.

| Ferramenta | Campos | O que faz |
|---|---|---|
| `create_sphere` | `radius`, `x`, `y`, `z`, `color`, `specular`, `roughness`, `name` | cria uma esfera UV |
| `create_cube` | `size`, ... | cria um cubo |
| `create_cylinder` | `radius`, `height`, ... | cria um cilindro |
| `create_cone` | `radius`, `height`, ... | cria um cone |
| `create_torus` | `major_radius`, `minor_radius`, ... | cria um toro |
| `list_objects` | nenhum | lista a cena em texto curto: `nome TIPO (x, y, z)` |
| `move_object` | `name` (obrigatório), `x`, `y`, `z`, `relative` | move um objeto existente pelo nome; eixos omitidos ficam parados |
| `bevel_object` | `name` (obrigatório), `width`, `segments` | adiciona/atualiza um modificador Bevel num objeto existente |

Regras de desenho (o objetivo do projeto):

- **Uma ferramenta por forma.** Escolher a forma já está no nome; não existe parâmetro `tipo` para errar.
- **Campos planos**, sem objeto dentro de objeto, com valores padrão.
- **O Python é um template fixo** dentro do binário. Só entram valores validados (números dentro de uma faixa, nome
  limpo, cor convertida). O nome do objeto entra sempre como texto escapado. O modelo não consegue injetar código.
- **Erros curtos e úteis.** Argumento inválido diz o que é válido; nome de objeto errado devolve a lista dos nomes
  reais, para o modelo se corrigir.

## Como funciona

```
modelo --MCP--> blender-minor (:9192) --MCP--> um servidor MCP do Blender (:9191) --> Blender
```

O `blender-minor` monta o script e o envia pela ferramenta `execute_blender_code` de um servidor MCP do Blender que já
exista. Fala JSON-RPC simples por HTTP (sem sessão, caminho `/`). Você precisa de um servidor MCP do Blender que exponha
`execute_blender_code` (usamos o projeto `blender-mcp` com a extensão dele no Blender).

## Começando

Requisitos: .NET 10 SDK, Blender com um servidor MCP do Blender rodando.

```bash
dotnet run                         # escuta em 127.0.0.1:9192
```

Variáveis de ambiente:

| Variável | Padrão | Significado |
|---|---|---|
| `BLENDER_MINOR_PORT` | `9192` | porta |
| `BLENDER_MINOR_HOST` | `127.0.0.1` | endereço de escuta (veja [segurança](#segurança)) |
| `BLENDER_MCP_URL` | `http://127.0.0.1:9191/` | o servidor MCP do Blender |
| `BLENDER_MINOR_DRYRUN` | desligado | `1` devolve o script gerado em vez de enviar (testa sem tocar no Blender) |
| `BLENDER_MINOR_TEMPLATES` | `~/.config/blender-minor/templates` | pasta de override dos templates |
| `BLENDER_BIN` | | executável do Blender usado pelo `--selftest` |

### Usando com o llama.cpp

O `llama-server` só inicia servidores MCP como **processos** (stdio), não por URL. Use a ponte incluída
(`tools/mcp_stdio_bridge.py`) num `mcp.json` com `"command": "python3"` e `"args": ["-I", ".../mcp_stdio_bridge.py", "http://127.0.0.1:9192/"]`,
e rode `llama-server -m modelo.gguf --jinja --mcp-servers-config mcp.json`. Os nomes das ferramentas ganham o prefixo do
servidor (`blender-minor_create_sphere`).

### Prompt de sistema

Modelos pequenos ainda precisam de uma instrução curta: veja [`prompts/system.pt-BR.txt`](prompts/system.pt-BR.txt) e
[`system.en.txt`](prompts/system.en.txt). As regras que mais importam: sempre chamar a ferramenta, nunca dizer que deu
certo sem um `OK`, sempre passar `color` quando uma cor for citada, copiar os nomes dos objetos exatamente.

## Quando o Blender mudar a API: override por arquivo

O Blender muda o contrato do `bpy` entre versões (no 5.3, `Specular` virou `Specular IOR Level`). O texto embutido é só o
**padrão da versão que usamos**; um arquivo em `~/.config/blender-minor/templates/` **sobrescreve** o padrão, sem recompilar
nem reiniciar (é lido a cada chamada; apagar o arquivo volta ao padrão).

```bash
dotnet BlenderMinor.dll --dump-templates   # grava os templates embutidos como ponto de partida
dotnet BlenderMinor.dll --selftest         # roda todas as ferramentas num Blender headless ISOLADO e diz o que quebrou
curl http://127.0.0.1:9192/templates       # de onde vem cada texto (arquivo ou embutido)
```

## Avaliação

[`eval/evaluate_model.py`](eval/evaluate_model.py) manda 12 pedidos a um `llama-server`, deixa o modelo chamar as
ferramentas e depois **confere a cena** (não o que o modelo diz): o objeto criado e a posição. Ele remove só o que criou.
Uma rodada de exemplo (20/11/2026, temperatura 0, sem raciocínio, 12 cenários, uma rodada cada: é só um sinal aproximado):

| Modelo | Chamou ferramenta | Argumentos todos certos | Criou de verdade |
|---|---|---|---|
| Qwen3-0.6B Q4_K_M, sem prompt de sistema | 12/12 | 2/12 | 10/11 |
| Qwen3-0.6B Q4_K_M, com `prompts/system.pt-BR.txt` | 12/12 | 9/12 | 11/11 |
| Qwen3-VL-2B-Instruct Q4_K_M (só texto), mesmo prompt | 12/12 | 10/12 | 10/11 |

A maior parte das falhas foi esquecer o campo `color`. O prompt de sistema corrige quase tudo.

Rode contra o seu próprio `llama-server` (iniciado com a ponte MCP acima):

```bash
python3 -I eval/evaluate_model.py --label meu-modelo --system prompts/system.pt-BR.txt            # pedidos em português (padrão)
python3 -I eval/evaluate_model.py --label meu-modelo --system prompts/system.en.txt --lang en     # os mesmos 12 pedidos em inglês
```

## Segurança

- A API **não tem autenticação**. Por padrão escuta só em `127.0.0.1`. `BLENDER_MINOR_HOST=0.0.0.0` deixa qualquer um que
  alcance a porta criar objetos no seu Blender; só faça isso numa rede em que você confia.
- O modelo nunca manda código. Só preenche campos validados de templates fixos.
- O servidor MCP do Blender por trás pode expor execução de Python arbitrário (`execute_blender_code`). **Não dê essa
  ferramenta ao modelo**; dê a ele só o `blender-minor`.

## Roteiro

Com o objetivo de modelar peças para impressão 3D por conversa, com medidas exatas e furos nos lugares certos:

- [ ] `create_box(width, depth, height, ...)` e `set_dimensions(name, x, y, z)`
- [ ] `boolean_op(target, tool, UNION | DIFFERENCE | INTERSECT)` e `drill_hole(name, diameter, depth, x, y, z, axis)`
- [ ] `check_mesh(name)`: malha fechada, volume, dimensões
- [ ] `export_stl(name, path)` e unidades em milímetros
- [ ] `create_plane`, `create_icosphere`, `set_color`, `delete_object`

Ideias e pull requests são bem-vindos.

## Licença

[MIT](LICENSE).
