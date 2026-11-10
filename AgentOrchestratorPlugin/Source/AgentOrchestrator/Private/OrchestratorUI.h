#pragma once

#include "CoreMinimal.h"
#include "Styling/SlateColor.h"

struct FOrchestratorEditor;
struct FOrchestratorGrant;
struct FManagerAgent;
struct FOrchestratorSnapshot;
class FOrchestratorStatus;

namespace OrchestratorUI
{
	FSlateColor AgentStateColor(const FString& State);
	FString OverallDotName(const FOrchestratorStatus& Status);
	FSlateColor OverallColor(const FOrchestratorStatus& Status);
	FSlateColor EditorColor(const FOrchestratorEditor& Editor);
	FText GrantActivity(const FOrchestratorGrant& Grant);
	FText GrantName(const FOrchestratorGrant& Grant);
	FText HoldSummary(const FOrchestratorSnapshot& Snapshot);
	FText SummaryText(const FOrchestratorSnapshot& Snapshot);
	FText TurnText(const FOrchestratorGrant& Lane);
	FText EditorTitle(const FOrchestratorEditor& Editor);
	FText EditorDetail(const FOrchestratorEditor& Editor);
	FText EditorWarning(const FOrchestratorEditor& Editor);
	FText TakePausesNames(const FOrchestratorSnapshot& Snapshot);
	FSlateColor ManagerStateColor(const FString& State);
	FText ManagerStateWord(const FManagerAgent& Agent);
	FSlateColor KindColor(const FString& Kind);
	FSlateColor MasterAgentColor();
}
