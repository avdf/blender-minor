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
>
> **Hardware antigo?** O add-on oficial Blender MCP pede Blender 5.1+, e o Blender 4.0+ precisa de OpenGL 4.3. Um
> patch que faz o add-on rodar no **Blender 3.6 LTS** (OpenGL 3.3) está em [`blender-3.6/`](blender-3.6/).

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
| `create_plane` | `size`, ... | cria um plano quadrado |
| `create_icosphere` | `radius`, `subdivisions`, ... | cria uma icosfera (esfera de triângulos) |
| `list_objects` | nenhum | lista a cena em texto curto: `nome TIPO (x, y, z)` |
| `move_object` | `name` (obrigatório), `x`, `y`, `z`, `relative` | move um objeto existente pelo nome; eixos omitidos ficam parados |
| `bevel_object` | `name` (obrigatório), `width`, `segments` | adiciona/atualiza um modificador Bevel num objeto existente |
| `array_object` | `name` (obrigatório), `count_x`, `count_y`, `gap`, `apply` | repete um objeto em grade com o modificador Array do Blender, para imprimir várias cópias de uma peça pequena e cada camada ter tempo de esfriar; por padrão as cópias viram parte do objeto (`apply`), então `check_mesh` e `export_stl` enxergam todas; no máximo 400 cópias |
| `create_box` | `name`, `width`, `depth`, `height`, `x`, `y`, `z`, `color` | uma caixa com medidas exatas (x, y, z = o centro dela); um `name` que você deu e já existe é recusado, em vez de virar `.001` |
| `set_dimensions` | `name` (obrigatório), `x`, `y`, `z` | define o tamanho exato de um objeto existente |
| `pad` | `name`, `height` (obrigatórios), `x1`, `x2`, `y1`, `y2`, `z1`, `z2`, `axis`, `side` | acrescenta material numa face, como o Pad do FreeCAD: um retângulo sobe `height`. O plano da face é a **superfície real sob o retângulo** (raios), então funciona também em peça com degrau; recusa um retângulo que não toca em nada e nunca responde OK quando nada mudou |
| `pocket` | `name`, `depth` (obrigatórios), `x1`, `x2`, `y1`, `y2`, `z1`, `z2`, `axis`, `side` | corta um rebaixo retangular numa face, como o Pocket do FreeCAD; mesmas regras de face do `pad` |
| `mirror_object` | `name` (obrigatório), `axis`, `at`, `merge`, `apply` | modificador Mirror do Blender: desenhe uma metade e a outra aparece; `at` = onde fica o plano (padrão: a origem do objeto) |
| `join` | `target`, `other` (obrigatórios) | união: o `other` entra no `target` e some; **transacional** |
| `subtract` | `target`, `cutter` (obrigatórios) | diferença: o cortador sai do `target` e depois é apagado; **transacional** |
| `intersect` | `target`, `other` (obrigatórios) | fica só com o que os dois têm em comum; **transacional** |
| `boolean_op` | `target`, `cutter` (obrigatórios), `operation`, `delete_cutter` | UNION / DIFFERENCE / INTERSECT de dois objetos, com a operação como parâmetro; **transacional** (veja abaixo). Para modelos pequenos prefira `join` / `subtract` / `intersect`: quem carrega a operação é a *ferramenta*, então o modelo nunca precisa escolher um valor |
| `drill_hole` | `name`, `diameter` (obrigatórios), `x`, `y`, `z`, `axis`, `side`, `depth`, `clearance`, `counterbore_diameter`, `counterbore_depth`, `countersink_diameter`, `segments` | furo redondo de diâmetro exato, passante ou cego, nos eixos X, Y ou Z, com **rebaixo** opcional (para a cabeça do parafuso) ou **escareado** de 90° |
| `screw_hole` | `name` (obrigatório), `size` (M2 a M8), `kind`, `x`, `y`, `z`, `axis`, `side`, `depth` | furo para um parafuso padrão, de uma tabela: `clearance` (passante), `counterbore` (rebaixo), `countersunk` (escareado) ou `insert` (furo cego para inserto de rosca a quente) |
| `set_color` | `name`, `color` (obrigatórios), `roughness`, `specular` | troca o material de um objeto existente |
| `repair_mesh` | `name` (obrigatório), `tolerance` | junta vértices duplicados, remove pedaços soltos, corrige normais, tapa buracos; mostra antes/depois; desfaz se piorar |
| `delete_object` | `name` (obrigatório) | apaga um objeto pelo nome |
| `export_stl` | `name` (obrigatório), `filename` | exporta um STL binário para o fatiador, **só um nome de arquivo** numa pasta de exportação fixa |
| `set_units` | `unit` (`mm`, `cm`, `m`) | ajusta a cena para 1 unidade do Blender = 1 mm (ou cm, m) |
| `check_mesh` | `name` (obrigatório) | malha fechada (manifold) ou não, volume e dimensões |

**As operações booleanas são transacionais.** Booleanas em malha de triângulos falham em malhas sujas. O `boolean_op` e o
`drill_hole` guardam uma cópia da malha, aplicam a operação (solver Exact), conferem se o resultado é uma malha fechada com
volume e, se não for, **devolvem a malha antiga com um erro claro**. O `drill_hole` corta com um cilindro que passa dos dois lados
da peça (sem faces coplanares); o furo é um polígono de 64 lados inscrito no círculo (um furo de 3 mm mede 2,996 mm entre as
faces planas), e o `clearance` soma ao diâmetro para impressão 3D.

**Tabela de parafusos.** O `screw_hole` usa valores típicos (folga ISO 273 média, rebaixo de cabeça cilíndrica ISO 4762, cabeça
chata ISO 10642, insertos de latão de rosca a quente comuns; para o M3: passante 3,4, rebaixo 6,0 x 3,0, inserto 4,0 x 5,7).
São um **ponto de partida**: confira com os seus parafusos, insertos e impressora. Qualquer campo pode ser sobrescrito, sem
recompilar, em `~/.config/blender-minor/screws.json` (ou `BLENDER_MINOR_SCREWS`), por exemplo
`{"M3": {"clearance": 3.5, "insert_diameter": 4.2}}`.

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

Modelos pequenos se saem melhor com uma **lista curta de ferramentas**, e o `llama-server` não tem lista de permissão
própria, então a ponte tem: passe `--allow` com os nomes das ferramentas que o modelo deve enxergar. O resto some do
`tools/list` e é recusado no `tools/call` sem nunca chegar ao blender-minor:

```json
"args": ["-I", ".../mcp_stdio_bridge.py", "http://127.0.0.1:9192/",
         "--allow", "create_box,drill_hole,screw_hole,list_objects"]
```

**Esconda também parâmetros.** Um modelo pequeno preenche parâmetro opcional por impulso: o Qwen3-1.7B mandava `axis: "X"` para o
`drill_hole`, e `side: "-"` para o `pad` (uma faixa levantada na face de baixo, e não na de cima), mesmo com um prompt dizendo
para não mandar. `--hide ferramenta.parametro,...` tira esses parâmetros do `tools/list` e os descarta de qualquer chamada, então
o modelo não consegue escolhê-los. Para o trabalho de peças:

```json
"--hide", "drill_hole.axis,drill_hole.side,screw_hole.axis,screw_hole.side,pad.axis,pad.side,pad.z1,pad.z2,pocket.axis,pocket.side,pocket.z1,pocket.z2"
```

O que o modelo não enxerga ele não erra, e ainda gasta menos tokens de prompt.

**A ordem das propriedades importa.** O `llama-server` transforma o esquema de cada ferramenta numa gramática que mantém a
ordem dos campos, e o modelo escreve o nome primeiro ("uma caixa chamada base, 60 por 40..."). Por isso toda ferramenta
publica o `name` como **primeira** propriedade. Com o `name` por último, o Qwen3-1.7B gerou `{"name": "base', " }` no
`create_box` e perdeu todas as medidas (0 de 3 peças certas); com o `name` primeiro o mesmo modelo acerta dimensões e
volumes (2 de 3, e o erro que sobrou foi de posicionamento).

### Prompt de sistema

Modelos pequenos ainda precisam de uma instrução curta: veja [`prompts/system.pt-BR.txt`](prompts/system.pt-BR.txt) e
[`system.en.txt`](prompts/system.en.txt). As regras que mais importam: sempre chamar a ferramenta, nunca dizer que deu
certo sem um `OK`, sempre passar `color` quando uma cor for citada, copiar os nomes dos objetos exatamente.

Para **peças com medida exata e furos** (impressão 3D, laser) existe um prompt mais curto que lista só as ferramentas
desse trabalho: [`prompts/system.parts.en.txt`](prompts/system.parts.en.txt) (em inglês). Ele combina com a lista
`--allow` da [ponte stdio](#usando-com-o-llamacpp) (`create_box`, `drill_hole`, `screw_hole`, `pad`, `pocket`,
`array_object`, `list_objects`, `check_mesh`, `export_stl`), então o modelo enxerga a mesma lista curta que o prompt
descreve. São nove ferramentas de propósito: cada ferramenta custa tokens de prompt (uns 2300 para oito ferramentas mais o
prompt, minutos numa CPU velha), e uma ferramenta que o prompt não descreve só confunde um modelo pequeno. Acrescente
`join`, `subtract`, `mirror_object` ou `move_object` na lista `--allow` **e** no prompt quando o trabalho precisar.

## Quando o Blender mudar a API: override por arquivo

O Blender muda o contrato do `bpy` entre versões (no 5.3, `Specular` virou `Specular IOR Level`). O texto embutido é só o
**padrão da versão que usamos**; um arquivo em `~/.config/blender-minor/templates/` **sobrescreve** o padrão, sem recompilar
nem reiniciar (é lido a cada chamada; apagar o arquivo volta ao padrão).

```bash
dotnet BlenderMinor.dll --dump-templates   # grava os templates embutidos como ponto de partida
dotnet BlenderMinor.dll --selftest         # roda todas as ferramentas num Blender headless ISOLADO e diz o que quebrou
curl http://127.0.0.1:9192/templates       # de onde vem cada texto (arquivo ou embutido)
```

## Testes

Três níveis, do mais rápido ao mais lento:

```bash
dotnet BlenderMinor.dll --selftest      # todas as ferramentas num Blender headless isolado (sem janela)
python3 -I eval/test_tools.py           # teste de integração contra um blender-minor e um Blender AO VIVO, sem modelo, ~20 s
python3 -I eval/evaluate_model.py ...   # quão bem um modelo usa as ferramentas (abaixo)
```

O `eval/test_tools.py` confere medidas exatas, o volume removido pelos furos (contra pi r^2 h), os volumes de união/interseção/
diferença, o desfazer de uma booleana quebrada, as mensagens de erro e tentativas de injeção pelo nome. Ele cria só objetos
`BMT_*` e remove todos.

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

Há mais duas verificações em `eval/`:

- `eval/evaluate_parts.py`: a mesma ideia para **peças**. Pede ao modelo caixas com furos, uma faixa levantada (`pad`) e um
  rebaixo (`pocket`); cada peça é lida de volta com `check_mesh` e o tamanho **e o volume** são comparados com os números feitos
  à mão. Os casos cujas ferramentas não estão na lista do modelo são pulados.
  `python3 -I eval/evaluate_parts.py --url http://host:9931 --label meu-modelo`
- `eval/check_tools.py`: chama as ferramentas **sem modelo** e confere cada resultado com um volume calculado (pad, pocket em
  peça com degrau, mirror, join, subtract, intersect, array). Ele apaga tudo o que cria, então é seguro rodar num Blender com
  trabalho seu aberto. `python3 -I eval/check_tools.py --url http://127.0.0.1:9192/`

## Segurança

- A API **não tem autenticação**. Por padrão escuta só em `127.0.0.1`. `BLENDER_MINOR_HOST=0.0.0.0` deixa qualquer um que
  alcance a porta criar objetos no seu Blender; só faça isso numa rede em que você confia.
- O modelo nunca manda código. Só preenche campos validados de templates fixos.
- O `export_stl` nunca aceita um caminho: só um nome de arquivo, gravado em `~/blender-minor-exports` (ou `BLENDER_MINOR_EXPORT_DIR`).
  É de propósito: um caminho livre deixaria o modelo gravar em qualquer lugar do disco.
- O `delete_object` é destrutivo. Se o seu cliente MCP deixa escolher quais ferramentas um modelo pode chamar, deixe-o de fora para modelos pequenos.
- O servidor MCP do Blender por trás pode expor execução de Python arbitrário (`execute_blender_code`). **Não dê essa
  ferramenta ao modelo**; dê a ele só o `blender-minor`.

## Roteiro

Com o objetivo de modelar peças para impressão 3D por conversa, com medidas exatas e furos nos lugares certos:

- [x] `create_box`, `set_dimensions`, `boolean_op` (transacional), `drill_hole` (passante/cego, folga), `check_mesh`
- [x] rebaixo e escareado no `drill_hole`, e o `screw_hole` com tabela de medidas padrão
- [x] `repair_mesh`, `export_stl` (só nome de arquivo, pasta de exportação fixa), `set_units`
- [x] `create_plane`, `create_icosphere`, `set_color`, `delete_object`
- [x] `array_object` (cópias em grade, para imprimir várias peças pequenas), `pad` / `pocket` (retângulos numa face), `mirror_object`, e `join` / `subtract` / `intersect`
- [ ] filetes (`bevel_object` em arestas escolhidas), bolsões redondos e rasgos, padrões de furos
- [ ] opção de furo roscado e profundidade do inserto configurável por material

Ideias e pull requests são bem-vindos.

## Licença

[MIT](LICENSE).
