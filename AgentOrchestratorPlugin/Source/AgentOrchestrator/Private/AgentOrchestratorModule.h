#pragma once

#include "CoreMinimal.h"
#include "HAL/IConsoleManager.h"
#include "Modules/ModuleInterface.h"

class FOrchestratorSelfTest;
class FOrchestratorStatus;
class FSpawnTabArgs;
class SDockTab;
class SOrchestratorChip;
class SOrchestratorPanel;

class FAgentOrchestratorModule : public IModuleInterface
{
public:
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;

private:
	void RegisterMenus();
	TSharedRef<SDockTab> SpawnPanelTab(const FSpawnTabArgs& Args);
	void OpenPanel();
	void WarnAboutLeftoverHold();
	void RunSelfTest();

	TSharedPtr<FOrchestratorStatus> Status;
	TSharedPtr<FOrchestratorSelfTest> SelfTest;
	TUniquePtr<FAutoConsoleCommand> SelfTestCommand;
	TWeakPtr<SOrchestratorPanel> Panel;
	TWeakPtr<SOrchestratorChip> Chip;
};
