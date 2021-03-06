[Code]

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
	xResultCode: Integer;
begin
	EnableFsRedirection(true);
	try
		Exec(ExpandConstant('{code:VSNET2010_PATH|}\devenv.exe'), '/setup', '', SW_HIDE, ewWaitUntilTerminated, xResultCode);
		if xResultCode <> 0 then
		begin
			RaiseException('An error occurred while unregistering Cosmos from Visual Studio!');
		end;	
	finally
		EnableFsRedirection(false);
	end;
end;