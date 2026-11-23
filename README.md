# Otimizador

Aplicativo para Windows x64 com diagnóstico de hardware, sensores ao vivo, análise de rede, benchmarks e ferramentas de otimização e reparo do Windows.

## Instalar

1. Baixe o [instalador do Otimizador 1.0.0](https://github.com/IsaacZimm/diagnostico-e-manutencao/releases/download/v1.0.0/Otimizador-1.0.0-windows-x64-setup.exe).
2. Feche qualquer versão aberta do aplicativo, execute o instalador e confirme a solicitação de administrador do Windows.
3. Abra **Otimizador** pelo menu Iniciar. Para remover o programa, procure por **Otimizador** em **Configurações → Aplicativos**.

O instalador inclui o **.NET 10 Desktop Runtime**. Para usar o programa, basta o instalador em um PC Intel ou AMD com Windows 11 x64 ou uma edição do Windows 10 x64 ainda compatível com o .NET 10. O Windows pede elevação ao abrir o aplicativo para operações como leitura de sensores e reparo do sistema. [Versões do Windows compatíveis com o .NET](https://learn.microsoft.com/pt-br/dotnet/core/install/windows#supported-versions)

A versão 1.0.0 é distribuída sem assinatura digital. O arquivo `.sha256` disponível na mesma release permite conferir a integridade do download.

## Recursos

- **Dados de componentes** reúne inventário do Windows, processador, placa de vídeo, memória e discos, sensores, energia e rede.
- **Jogos** reúne biblioteca de jogos instalados, estimativas de FPS, análise de gargalo e overlay.
- **Teste de desempenho** reúne benchmarks, testes de estresse e a aba Overclock. A disponibilidade das medições depende do hardware e dos drivers.
- **Ferramentas** reúne otimização, programas de inicialização, instalação de aplicativos, drivers, integração RGB e reparo do Windows.
- **Maker** reúne diagnóstico de portas COM e dispositivos USB-seriais, com acesso às páginas dos fabricantes para baixar os drivers.

### Reparo do Windows

Abra **Ferramentas → Reparo do Windows**, escolha um atalho ou ajuste as caixas individualmente. O painel mostra os comandos completos antes de rodar. As etapas marcadas seguem esta ordem:

1. `dism.exe /Online /Cleanup-Image /CheckHealth` consulta se já há corrupção registrada na imagem do Windows.
2. `dism.exe /Online /Cleanup-Image /ScanHealth` verifica o repositório de componentes.
3. `dism.exe /Online /NoRestart /Cleanup-Image /RestoreHealth` verifica e repara o repositório de componentes. O Windows pode usar o Windows Update para obter arquivos de reparo.
4. `sfc.exe /scannow` verifica e repara os arquivos protegidos do sistema.

O atalho **Reparo padrão** marca `RestoreHealth` e `SFC`. Se quiser reiniciar depois do reparo, marque a opção antes de executar. Ao concluir a sequência, o programa dá **30 segundos para cancelar** a reinicialização. [Documentação do DISM](https://learn.microsoft.com/pt-br/windows-hardware/manufacture/desktop/repair-a-windows-image?view=windows-11) · [Suporte do SFC](https://support.microsoft.com/pt-br/windows/experience/backup-recovery/using-system-file-checker-in-windows)

### Recursos opcionais

- Algumas leituras internas da CPU dependem do [driver PawnIO](https://pawnio.eu/). Para instalá-lo, rode `winget install --id namazso.PawnIO --exact --source winget` e reabra o aplicativo.
- O catálogo **Instalar apps** usa o WinGet, que vem no [Instalador de Aplicativo da Microsoft](https://learn.microsoft.com/pt-br/windows/package-manager/winget/).
- Drivers USB-seriais ficam disponíveis pelas páginas oficiais em **Maker → Drivers seriais**. O instalador público leva o Otimizador e o runtime, e os drivers são obtidos conforme o hardware.

## Compilar a partir do código-fonte

Instale o **SDK do .NET 10** no Windows:

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
```

Reabra o PowerShell, navegue até a raiz do repositório e rode:

```powershell
dotnet build Otimizador.sln -c Release
```

O SDK já inclui o runtime de desktop. Para executar um build dependente de framework em outro computador, instale somente o desktop runtime com `winget install --id Microsoft.DotNet.DesktopRuntime.10 --exact --source winget`. [Instalação do .NET no Windows](https://learn.microsoft.com/pt-br/dotnet/core/install/windows#install-with-windows-package-manager-winget)

### Gerar o instalador

Com o SDK instalado, instale o NSIS e execute o script no PowerShell:

```powershell
winget install --id NSIS.NSIS --exact --source winget
.\installer\compilar.ps1
```

O script publica o aplicativo para `win-x64` com o runtime incluso e gera `artifacts/v1.0.0/Otimizador-1.0.0-windows-x64-setup.exe` e o arquivo `.sha256`. Para apontar para outra instalação do NSIS, passe `-NsisCompiler` com o caminho do `makensis.exe`.

## Código e versões anteriores

O Otimizador usa código do projeto [ResetExplode, de GustavoReset](https://github.com/GustavoReset/ResetExplode), sob licença MIT. Consulte [LICENSE](LICENSE), [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) e a pasta [licenses](licenses). A versão anterior do **Diagnóstico e Manutenção** está na [release v0.8.0](https://github.com/IsaacZimm/diagnostico-e-manutencao/releases/tag/v0.8.0).
