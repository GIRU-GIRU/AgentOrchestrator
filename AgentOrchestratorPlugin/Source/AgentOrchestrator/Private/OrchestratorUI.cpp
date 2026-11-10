#include "OrchestratorUI.h"

#include "OrchestratorStatus.h"
#include "Styling/StyleColors.h"

#define LOCTEXT_NAMESPACE "AgentOrchestrator"

namespace OrchestratorUI
{
	FSlateColor AgentStateColor(const FString& State)
	{
		if (State == TEXT("busy"))
		{
			return FStyleColors::AccentOrange;
		}
		if (State == TEXT("working") || State == TEXT("background"))
		{
			return FStyleColors::AccentYellow;
		}
		if (State == TEXT("waiting-user"))
		{
			return FStyleColors::AccentBlue;
		}
		if (State == TEXT("hung") || State == TEXT("stale-background"))
		{
			return FStyleColors::Error;
		}
		return FStyleColors::AccentGray;
	}

	FString OverallDotName(const FOrchestratorStatus& Status)
	{
		return Status.IsStale() ? FString(TEXT("grey")) : Status.Get().Summary.Dot;
	}

	FSlateColor OverallColor(const FOrchestratorStatus& Status)
	{
		FString Dot = OverallDotName(Status);
		if (Dot == TEXT("grey"))
		{
			return FStyleColors::AccentGray;
		}
		if (Dot == TEXT("red"))
		{
			return FStyleColors::Error;
		}
		if (Dot == TEXT("blue"))
		{
			return FStyleColors::AccentBlue;
		}
		return Dot == TEXT("orange") ? FStyleColors::AccentOrange : FStyleColors::Success;
	}

	FSlateColor EditorColor(const FOrchestratorEditor& Editor)
	{
		if (Editor.bFrozen)
		{
			return FStyleColors::Error;
		}
		return Editor.Pie.Get(false) ? FStyleColors::AccentOrange : FStyleColors::Success;
	}

	FText GrantName(const FOrchestratorGrant& Grant)
	{
		return FText::FromString(Grant.DisplayName);
	}

	FText GrantActivity(const FOrchestratorGrant& Grant)
	{
		return FText::FromString(Grant.Activity);
	}

	FText SummaryText(const FOrchestratorSnapshot& Snapshot)
	{
		const FOrchestratorSummary& Summary = Snapshot.Summary;
		if (Summary.State == TEXT("hold"))
		{
			return HoldSummary(Snapshot);
		}
		if (Summary.State == TEXT("pie") && Summary.PiePid != FPlatformProcess::GetCurrentProcessId())
		{
			return FText::Format(LOCTEXT("PieElsewhere", "PIE in another editor (pid {0}) - no agent waiting"), FText::AsNumber(Summary.PiePid, &FNumberFormattingOptions::DefaultNoGrouping()));
		}
		return FText::FromString(Summary.Text);
	}

	FText TurnText(const FOrchestratorGrant& Lane)
	{
		if (Lane.TurnDueAt <= 0.0)
		{
			return FText::GetEmpty();
		}
		double Now = FOrchestratorStatus::NowUnix();
		if (Now < Lane.TurnDueAt)
		{
			return FText::Format(LOCTEXT("TurnLeft", "Its turn: {0} left, others are waiting"), FOrchestratorStatus::FormatDuration(Lane.TurnDueAt - Now));
		}
		return FText::Format(LOCTEXT("TurnOver", "Its turn ended {0} ago: it can only finish now, and the editor comes back within {1}"),
			FOrchestratorStatus::FormatAge(Lane.TurnDueAt), FOrchestratorStatus::FormatDuration(Lane.TurnBackAt - Now));
	}

	FText EditorTitle(const FOrchestratorEditor& Editor)
	{
		FText Role = Editor.Role == TEXT("main") ? LOCTEXT("RoleMain", "Main editor")
			: Editor.Role == TEXT("headless") ? LOCTEXT("RoleHeadless", "Headless editor (rendering off)")
			: LOCTEXT("RoleSecond", "Second editor");
		FText Pid = FText::AsNumber(Editor.Pid, &FNumberFormattingOptions::DefaultNoGrouping());
		return Editor.IsThisWindow()
			? FText::Format(LOCTEXT("EditorTitleThis", "{0} - this window (pid {1})"), Role, Pid)
			: FText::Format(LOCTEXT("EditorTitleOther", "{0} (pid {1})"), Role, Pid);
	}

	FText EditorDetail(const FOrchestratorEditor& Editor)
	{
		FText Pie = !Editor.Pie.IsSet() ? LOCTEXT("PieUnknown", "PIE unknown") : Editor.Pie.GetValue() ? LOCTEXT("PieRunning", "PIE running") : LOCTEXT("PieNotRunning", "no PIE");
		return Editor.OnItName.IsEmpty() ? Pie : FText::Format(LOCTEXT("EditorOnIt", "{0} - on it: {1}"), Pie, FText::FromString(Editor.OnItName));
	}

	FText EditorWarning(const FOrchestratorEditor& Editor)
	{
		if (Editor.NotRespondingSince <= 0.0)
		{
			return FText::GetEmpty();
		}
		return FText::Format(Editor.bFrozen
			? LOCTEXT("EditorFrozen", "Frozen: not responding for {0}. MASTER AGENT clears a frozen editor.")
			: LOCTEXT("EditorNotResponding", "Not responding for {0}; a long load does this too."), FOrchestratorStatus::FormatAge(Editor.NotRespondingSince));
	}

	FText TakePausesNames(const FOrchestratorSnapshot& Snapshot)
	{
		return FText::FromString(FString::Join(Snapshot.Summary.TakePauses, TEXT(", ")));
	}

	/** Agent Manager's own colours (its app.css), so an agent reads the same on both screens: green working, red needs you, yellow asked, orange queued, purple on the editor, grey quiet. */
	FSlateColor ManagerStateColor(const FString& State)
	{
		if (State == TEXT("Working"))
		{
			return FLinearColor(FColor::FromHex(TEXT("3DDC84")));
		}
		if (State == TEXT("NeedsInput"))
		{
			return FLinearColor(FColor::FromHex(TEXT("FF5D6C")));
		}
		if (State == TEXT("Asked"))
		{
			return FLinearColor(FColor::FromHex(TEXT("F2C94C")));
		}
		if (State == TEXT("LaneBlocked"))
		{
			return FLinearColor(FColor::FromHex(TEXT("FF9F43")));
		}
		if (State == TEXT("UsingEditor"))
		{
			return FLinearColor(FColor::FromHex(TEXT("B44DFF")));
		}
		return FLinearColor(FColor::FromHex(TEXT("6C7382")));
	}

	FText ManagerStateWord(const FManagerAgent& Agent)
	{
		if (Agent.State == TEXT("NeedsInput"))
		{
			return LOCTEXT("StateNeedsYou", "NEEDS YOU");
		}
		if (Agent.State == TEXT("Asked"))
		{
			return LOCTEXT("StateAsked", "ASKED");
		}
		if (Agent.State == TEXT("LaneBlocked"))
		{
			return LOCTEXT("StateQueued", "QUEUED");
		}
		if (Agent.State == TEXT("UsingEditor"))
		{
			return LOCTEXT("StateEditor", "HAS THE EDITOR");
		}
		if (Agent.State == TEXT("Working"))
		{
			return LOCTEXT("StateWorking", "WORKING");
		}
		return FText::FromString(Agent.State.ToUpper());
	}

	/** The makers' colours, as the manager marks each kind: Claude's coral, OpenAI's white. */
	FSlateColor KindColor(const FString& Kind)
	{
		return Kind == TEXT("Claude") ? FLinearColor(FColor::FromHex(TEXT("D97757"))) : FLinearColor(FColor::FromHex(TEXT("EDEDED")));
	}

	FSlateColor MasterAgentColor()
	{
		return FLinearColor(FColor::FromHex(TEXT("4FD8FF")));
	}

	FText HoldSummary(const FOrchestratorSnapshot& Snapshot)
	{
		int32 Waiting = Snapshot.Hold.PausedNames.Num();
		if (Waiting == 0)
		{
			return FText::Format(LOCTEXT("HoldNoneWaiting", "You have the editor ({0}) - agents paused"), FOrchestratorStatus::FormatAge(Snapshot.Hold.Since));
		}
		return FText::Format(LOCTEXT("HoldWaiting", "You have the editor ({0}) - {1} {1}|plural(one=agent,other=agents) waiting"),
			FOrchestratorStatus::FormatAge(Snapshot.Hold.Since), Waiting);
	}
}

#undef LOCTEXT_NAMESPACE
