# Port do add-on oficial Blender MCP para o Blender 3.6 LTS

O `blender-minor` conversa com o Blender pelo add-on oficial **Blender MCP**, do Blender Lab
(<https://projects.blender.org/lab/blender_mcp>). Esse add-on pede **Blender 5.1 ou mais novo**, e o
Blender 4.0 em diante precisa de **OpenGL 4.3** para abrir a janela. Em máquinas antigas (por exemplo uma
Intel HD 4000, que chega só ao OpenGL 4.2) nenhum dos dois abre. O **Blender 3.6 LTS precisa só do OpenGL 3.3**
e abre nativo nesse hardware. Esta pasta faz o add-on oficial rodar no 3.6.

## Licença

O patch deriva do add-on do Blender Lab, que é **GPL-3.0-ou-posterior**, e mantém essa licença (veja
[`LICENSE-GPL-3.0`](LICENSE-GPL-3.0)). Os arquivos do add-on oficial **não** são copiados para este repositório:
você aplica o patch no seu próprio clone. O resto do repositório continua MIT.

## O que tem aqui

| Arquivo | O que é |
|---|---|
| `blender-mcp-addon-3.6.patch` | diff contra o commit oficial `dbbf836ad4b1025f14a2b3b504c43903f39e0b04` (4 arquivos, +68 / -16) |
| `serve_background.py` | sobe o servidor do add-on dentro do Blender **sem janela** (no lugar do `blender --command blender_mcp`, que não existe no 3.6) |
| `test_mcp_blender36.py` | teste rápido: servidor MCP oficial -> add-on -> Blender 3.6 (6 verificações) |
| `LICENSE-GPL-3.0` | licença do patch |

## O que o patch muda

1. **`bl_info`**: o 3.6 não tem sistema de extensões e não lê o `blender_manifest.toml`.
2. **`bpy.app.online_access`** (Blender 4.2+) é lido com `getattr(..., True)`.
3. **`bpy.utils.register_cli_command`** / `unregister_cli_command` (não existem no 3.6) só são usados se existirem.
4. **`typing.Self`** (Python 3.11+; o Blender 3.6 traz o Python 3.10) cai para uma anotação simples.
5. **A proteção (weak sandbox) agora realmente bloqueia `bpy.ops.wm.read_factory_settings` no 3.6.** O original
   se engancha em `bpy.ops._op_create_function`, que o 3.6 não tem; então o bloqueio não fazia nada, e a chamada
   recarregava as preferências, desligava o add-on e **derrubava o servidor MCP**. O patch usa o equivalente do 3.6
   (`_bpy_ops_submodule__getattr__`) e dá erro se não reconhecer nenhum dos dois formatos. Atenção: não detecte o
   gancho com `hasattr(bpy.ops, ...)`, porque o `bpy.ops` tem um `__getattr__` que aceita qualquer nome e o `hasattr`
   sempre dá verdadeiro; o patch olha `vars(bpy.ops)`.
6. **A cena é atualizada ao fim de cada execução** (`view_layer.update()`): sem janela nada redesenha, então
   `Object.dimensions` ficava velho depois de mudar `scale`. Agora a chamada *seguinte* lê o valor certo.

## Como usar

```bash
# 1) o add-on oficial, no commit para o qual o patch foi feito
git clone https://projects.blender.org/lab/blender_mcp.git
cd blender_mcp && git checkout dbbf836ad4b1025f14a2b3b504c43903f39e0b04

# 2) aplicar o patch dentro da pasta addon/
cd addon && patch -p1 < /caminho/para/blender-minor/blender-3.6/blender-mcp-addon-3.6.patch

# 3) uma pasta de scripts para o Blender 3.6 (nada é instalado no seu ~/.config)
mkdir -p ~/blender36-scripts/addons && cp -r blender_mcp_addon ~/blender36-scripts/addons/

# 4) subir o servidor no Blender 3.6, sem janela, numa porta à sua escolha
BLENDER_USER_SCRIPTS=~/blender36-scripts \
  /caminho/para/blender-3.6/blender -b --factory-startup \
  --python /caminho/para/blender-minor/blender-3.6/serve_background.py -- --port 9877
```

O servidor MCP oficial acha o add-on por `BLENDER_MCP_HOST` / `BLENDER_MCP_PORT` (padrão `localhost:9876`) e abre um
Blender em segundo plano para as ferramentas `*_for_cli` pelo `BLENDER_PATH`; aponte o `BLENDER_PATH` para o binário
do 3.6. Depois rode o teste (o cabeçalho dele explica).

## Situação

Testado no **Blender 3.6.23 (Python 3.10.13)**, Debian 13, Intel HD Graphics 4000 (OpenGL 4.2), sem janela:

- funciona: `execute_blender_code`, `get_objects_summary`, `execute_blender_code_for_cli` (exige um `blend_file`),
  dimensões exatas e furo por Boolean, `read_factory_settings` bloqueado com o servidor continuando no ar;
- **não testado**: o modo interativo com janela (início automático nas preferências) e as ferramentas de captura de
  tela e de "ir para" (precisam de janela);
- limitação conhecida: `bpy.data.file_path_foreach` não existe no 3.6 (nem no 4.3), então a ferramenta de
  "arquivos faltando" não funciona.

É um port feito para hardware antigo, não uma versão oficial: relate problemas aqui, não ao Blender Lab.
