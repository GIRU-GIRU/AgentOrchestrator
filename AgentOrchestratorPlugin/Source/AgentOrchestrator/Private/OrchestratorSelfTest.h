#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"

class FJsonObject;
class FOrchestratorStatus;
class SOrchestratorChip;
class SOrchestratorPanel;
class SWidget;

/**
 * Orch.SelfTest: reads back what the chip and the panel really show and writes it to Saved/Orchestration/editors/, so the
 * Master Agent can check them without anyone looking. It runs over several frames, because a tab it has just opened is not on screen until
 * Slate lays it out, and selftest.json is written last, so its appearance means the run is over. It sets no hold and runs no orch command.
 */
class FOrchestratorSelfTest : public TSharedFromThis<FOrchestratorSelfTest>
{
public:
	FOrchestratorSelfTest(TSharedRef<FOrchestratorStatus> InStatus, TFunction<TSharedPtr<SOrchestratorPanel>()> InFindPanel,
		TFunction<TSharedPtr<SOrchestratorChip>()> InFindChip, FSimpleDelegate InOpenPanel);
	~FOrchestratorSelfTest();

	void Start();
	bool IsRunning() const { return TickerHandle.IsValid(); }

private:
	static constexpr double PANEL_WAIT_SECONDS = 15.0;
	static constexpr int32 SETTLE_FRAMES = 3;

	enum class EStage : uint8
	{
		Chip,
		WaitForPanel,
		Finish,
	};

	bool Tick(float DeltaTime);
	void ReportChip();
	void ReportPanel(SOrchestratorPanel& Panel);
	void ReportTakeLogic();
	void ReportRunOrch();
	void WriteReport();
	void Capture(const TSharedRef<SWidget>& Widget, const TCHAR* FileName, bool bRequired);

	TSharedRef<FOrchestratorStatus> Status;
	TFunction<TSharedPtr<SOrchestratorPanel>()> FindPanel;
	TFunction<TSharedPtr<SOrchestratorChip>()> FindChip;
	FSimpleDelegate OpenPanel;
	FTSTicker::FDelegateHandle TickerHandle;
	TSharedPtr<FJsonObject> Report;
	TSharedPtr<FJsonObject> Screenshots;
	TArray<FString> Failures;
	EStage Stage = EStage::Chip;
	double PanelDeadline = 0.0;
	int32 FramesWaited = 0;
};
