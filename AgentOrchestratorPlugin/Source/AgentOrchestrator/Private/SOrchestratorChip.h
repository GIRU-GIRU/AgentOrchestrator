#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class FOrchestratorStatus;

/** The level editor toolbar's one-glance view: who has the editor, plus the take / release toggle. */
class SOrchestratorChip : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SOrchestratorChip) {}
		SLATE_EVENT(FSimpleDelegate, OnOpenPanel)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs, TSharedRef<FOrchestratorStatus> InStatus);

	FText GetSummaryText() const;
	FText GetSummaryToolTip() const;
	FText GetToggleText() const;

private:
	static constexpr float SUMMARY_MAX_WIDTH = 300.f;
	static constexpr float DOT_SIZE = 10.f;

	FSlateColor GetDotColor() const;
	FText GetToggleToolTip() const;
	FReply OnToggleClicked();

	TSharedPtr<FOrchestratorStatus> Status;
	FSimpleDelegate OpenPanel;
};
