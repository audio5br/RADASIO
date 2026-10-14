#!/usr/bin/env python3
"""Aplica a marca definida em rad/branding.json ao codigo do FlexASIO.

Uso (na raiz do repositorio, depois de copiar o kit por cima do fork):
    python rad/apply_branding.py

E idempotente: rodar de novo nao estraga nada. Depois de rodar, revise com
`git diff` e faca commit. Nao rode isso dentro do CI (a versao do build ficaria
marcada como "dirty").
"""
import json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
cfg = json.loads((ROOT / "rad" / "branding.json").read_text(encoding="utf-8"))
G = cfg["guids"]

# Valores que vao parar dentro de literais C/INI/RGS: nada de aspas ou barras.
for key in ("brand", "driver_name", "publisher", "file_base", "reg_vendor", "installer_basename"):
    if re.search(r"[\"'\\%]", cfg[key]):
        sys.exit(f"ERRO: '{key}' nao pode conter aspas, barra invertida ou %.")
if len(cfg["driver_name"].encode()) > 31:
    sys.exit("ERRO: 'driver_name' tem no maximo 31 bytes (limite do ASIO).")
if not re.fullmatch(r"[A-Za-z0-9_]+", cfg["file_base"]):
    sys.exit("ERRO: 'file_base' so aceita letras, numeros e _.")
if "example.com" in (cfg["url"] + cfg["support_url"] + cfg["docs_url"]):
    print("AVISO: url/support_url/docs_url ainda sao exemplos (example.com).")

def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8")

def write(rel, text):
    (ROOT / rel).write_text(text, encoding="utf-8", newline="")

def sub(rel, old, new, label):
    text = read(rel)
    if new in text:  # verificar "ja aplicado" primeiro: o trecho novo pode conter o antigo
        print(f"  skip {rel}: {label} (ja aplicado)")
    elif old in text:
        write(rel, text.replace(old, new))
        print(f"  ok   {rel}: {label}")
    else:
        sys.exit(f"ERRO: nao achei o trecho esperado em {rel} ({label}). "
                 "O FlexASIO mudou? Verifique a versao do fork.")

FA = "src/flexasio/FlexASIO"
OLD = {"clsid": "462F2ABF-5278-436A-95B6-72CBF65482AE",
       "typelib": "DCF8D18D-F399-49C8-8286-E84CA8AD7729",
       "interface": "1C653355-6D85-43AD-9674-05D4549F19C1"}

print("GUIDs / registro COM")
for k, old in OLD.items():
    sub(f"{FA}/flexasio.idl", old, G[k], f"GUID {k}")

progid = f"{cfg['file_base'].lower()}.{cfg['file_base']}.1"
write(f"{FA}/flexasio.rgs", f"""HKCR
{{
\tForceRemove {progid} = s '{cfg['driver_name']}'
\t{{
\t\tCLSID = s '{{{G['clsid']}}}'
\t}}

\tNoRemove CLSID
\t{{
\t\tForceRemove {{{G['clsid']}}} = s '{cfg['driver_name']}'
\t\t{{
\t\t\tProgID = s '{progid}'
\t\t\tInprocServer32 = s '%MODULE%'
\t\t\t{{
\t\t\t\tval ThreadingModel = s 'Both'
\t\t\t}}
\t\t}}
\t}}
}}

HKLM
{{
\tNoRemove SOFTWARE
\t{{
\t\tNoRemove ASIO
\t\t{{
\t\t\tForceRemove '{cfg['driver_name']}'
\t\t\t{{
\t\t\t\tval CLSID = s '{{{G['clsid']}}}'
\t\t\t\tval Description = s '{cfg['driver_name']}'
\t\t\t}}
\t\t}}
\t}}
}}""")
print("  ok   flexasio.rgs regravado")

print("Nome do driver, arquivos e painel de controle")
sub(f"{FA}/cflexasio.cpp", 'strcpy_s(name, 32, "FlexASIO");', f'strcpy_s(name, 32, "{cfg["driver_name"]}");', "nome do driver")
sub(f"{FA}/config.cpp", 'L"FlexASIO.toml"', f'L"{cfg["file_base"]}.toml"', "arquivo de configuracao")
sub(f"{FA}/log.cpp", 'path.append("FlexASIO.log");', f'path.append("{cfg["file_base"]}.log");', "arquivo de log")
sub(f"{FA}/control_panel.cpp", r'L"Software\\Fabrikat\\FlexASIOGUI\\Install"',
    rf'L"Software\\{cfg["reg_vendor"]}\\Settings\\Install"', "chave de registro do app")
sub(f"{FA}/control_panel.cpp", r'L"\\FlexASIOGUI.exe"', rf'L"\\{cfg["file_base"]}Settings.exe"', "executavel do app")
sub(f"{FA}/control_panel.cpp",
    'std::wstring(L"https://github.com/dechamps/FlexASIO/blob/") + ConvertFromUTF8(::dechamps_CMakeUtils_gitDescription) + L"/CONFIGURATION.md"',
    f'std::wstring(L"{cfg["docs_url"]}")', "URL da documentacao")

print("Opcao sampleRate (taxa de amostragem definida pelo painel)")
# O FlexASIO original informa ao DAW a taxa padrao do dispositivo no Windows. Com esta
# opcao, `sampleRate = N` no arquivo .toml passa a ser a taxa informada ao abrir o driver.
sub(f"{FA}/config.h", "std::optional<int64_t> bufferSizeSamples;",
    "std::optional<int64_t> bufferSizeSamples;\n\t\tstd::optional<int64_t> sampleRate;", "campo sampleRate")
sub(f"{FA}/config.h", "bufferSizeSamples == other.bufferSizeSamples &&",
    "bufferSizeSamples == other.bufferSizeSamples &&\n\t\t\t\tsampleRate == other.sampleRate &&",
    "sampleRate na comparacao (dispara o reset ao mudar)")
sub(f"{FA}/config.cpp", "void SetStream(const toml::Table& table, Config::Stream& stream) {",
    "void ValidateSampleRate(const int64_t& sampleRate) {\n"
    "\t\t\tif (sampleRate <= 0) throw std::runtime_error(\"sample rate must be strictly positive\");\n"
    "\t\t\tif (sampleRate > 1'000'000) throw std::runtime_error(\"sample rate is too large\");\n"
    "\t\t}\n\n"
    "\t\tvoid SetStream(const toml::Table& table, Config::Stream& stream) {", "validacao do sampleRate")
sub(f"{FA}/config.cpp", 'SetOption(table, "bufferSizeSamples", config.bufferSizeSamples, ValidateBufferSize);',
    'SetOption(table, "bufferSizeSamples", config.bufferSizeSamples, ValidateBufferSize);\n'
    '\t\t\tSetOption(table, "sampleRate", config.sampleRate, ValidateSampleRate);', "leitura da opcao sampleRate")
sub(f"{FA}/flexasio.cpp",
    "ASIOSampleRate GetDefaultSampleRate(const std::optional<Device>& inputDevice, const std::optional<Device>& outputDevice) {",
    "ASIOSampleRate GetDefaultSampleRate(const Config& config, const std::optional<Device>& inputDevice, const std::optional<Device>& outputDevice) {\n"
    "\t\t\tif (config.sampleRate.has_value()) {\n"
    "\t\t\t\t// Taxa definida no arquivo de configuracao tem prioridade sobre a do Windows.\n"
    "\t\t\t\tLog() << \"Using sample rate \" << *config.sampleRate << \" Hz from configuration\";\n"
    "\t\t\t\treturn static_cast<ASIOSampleRate>(*config.sampleRate);\n"
    "\t\t\t}",
    "taxa da configuracao tem prioridade")
sub(f"{FA}/flexasio.cpp", "sampleRate(GetDefaultSampleRate(inputDevice, outputDevice))",
    "sampleRate(GetDefaultSampleRate(config, inputDevice, outputDevice))", "chamada com a configuracao")

print("Build (CMake) e metadados da DLL")
sub(f"{FA}/CMakeLists.txt", 'PROJECT_DESCRIPTION="FlexASIO ASIO Driver DLL"',
    f'PROJECT_DESCRIPTION="{cfg["brand"]} ASIO Driver DLL"', "descricao da DLL")
sub(f"{FA}/CMakeLists.txt", "install(TARGETS FlexASIO RUNTIME DESTINATION bin)",
    f"set_target_properties(FlexASIO PROPERTIES OUTPUT_NAME {cfg['file_base']})\ninstall(TARGETS FlexASIO RUNTIME DESTINATION bin)",
    "nome do arquivo .dll")
RC = "src/flexasio/versioninfo.rc"
sub(RC, 'VALUE "ProductName", "FlexASIO https://github.com/dechamps/FlexASIO"',
    f'VALUE "CompanyName", "{cfg["publisher"]}"\n\t\t\tVALUE "ProductName", "{cfg["brand"]}"', "ProductName/CompanyName")
sub(RC, 'VALUE "LegalCopyright", "MIT License, Copyright (c) 2018 Etienne Dechamps <etienne@edechamps.fr>"',
    f'VALUE "LegalCopyright", "{cfg["brand"]}. Based on FlexASIO: MIT License, Copyright (c) 2018 Etienne Dechamps <etienne@edechamps.fr>"',
    "copyright (mantem o aviso MIT original)")

print("Instalador")
sub("src/installer.cmake", "/FFlexASIO-${FLEXASIO_VERSION}", f"/F{cfg['installer_basename']}-${{FLEXASIO_VERSION}}", "nome do .exe")
tpl = read("rad/installer.in.iss")
for k, v in {"BRAND": cfg["brand"], "PUBLISHER": cfg["publisher"], "URL": cfg["url"],
             "SUPPORT_URL": cfg["support_url"], "FILE_BASE": cfg["file_base"],
             "REG_VENDOR": cfg["reg_vendor"], "APPID": G["appid"]}.items():
    tpl = tpl.replace(f"%%{k}%%", v)
write("src/installer.in.iss", tpl)
print("  ok   src/installer.in.iss gerado")
print("\nPronto. Revise com `git diff` e faca commit.")
