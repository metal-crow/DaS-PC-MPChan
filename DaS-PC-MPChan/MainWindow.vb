﻿Imports System.Collections.Specialized
Imports System.Threading
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Net.Sockets
Imports System.ComponentModel
Imports System.Text
Imports Microsoft.Win32
Imports Microsoft.VisualBasic
Imports Microsoft.VisualBasic.CompilerServices
Imports System.Xml

Structure BlockTypes
    Const GlobalBlock = "_G"
    Const ManualBlock = "_M"
    Const AgeBlock = "_A"
    Const OnHitHackBlock = "_C"
End Structure

Public Class MainWindow
    'Timers
    Private WithEvents updateActiveNodesTimer As New System.Windows.Forms.Timer()
    Private WithEvents checkOnHitPacketStorageTimer As New System.Windows.Forms.Timer()
    Private WithEvents damageLogTimer As New System.Windows.Forms.Timer()
    Private WithEvents updateUITimer As New System.Windows.Forms.Timer()
    Private WithEvents updateNetNodesTimer As New System.Windows.Forms.Timer()
    Private WithEvents updateOnlineStateTimer As New System.Windows.Forms.Timer()
    Private WithEvents netNodeConnectTimer As New System.Windows.Forms.Timer()
    Private WithEvents publishNodesTimer As New System.Windows.Forms.Timer()
    Private WithEvents checkWatchNodeTimer As New System.Windows.Forms.Timer()
    Private WithEvents dsAttachmentTimer As New System.Windows.Forms.Timer()
    Private WithEvents hotkeyTimer As New System.Windows.Forms.Timer()

    'For hotkey support
    Public Declare Function GetAsyncKeyState Lib "user32" (ByVal vKey As Integer) As Short

    'Hotkeys
    Dim ctrlHeld As Boolean
    Dim oneHeld As Boolean
    Dim twoheld As Boolean

    Public Version As String

    Private random As New Random()

    Private optionsLoaded As Boolean = False

    Private dsProcess As DarkSoulsProcess = Nothing
    Private _netClient As NetClient = Nothing
    Private netNodeDisplayList As New DSNodeBindingList()
    Private activeNodesDisplayList As New DSNodeBindingList()
    Private connectedNodes As New Dictionary(Of String, ConnectedNode)
    Private watchSteamId As String = Nothing
    Private watchExchangedLastTime As Date = DateTime.UTCNow

    Private manualConnections As New HashSet(Of String)

    Private recentConnections As New Queue(Of Tuple(Of Date, String))

    Private WhitelistLocation = $"{My.Application.Info.DirectoryPath}\whitelist.txt"

    Private Sub DSCM_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        lbxDebugLog.Load(Me)
        Version = lblVer.Text

        Dim oldFileArg As String = Nothing
        For Each arg In Environment.GetCommandLineArgs().Skip(1)
            If arg.StartsWith("--old-file=") Then
                oldFileArg = arg.Substring("--old-file=".Length)
            Else
                MsgBox("Unknown command line arguments")
                oldFileArg = Nothing
                Exit For
            End If
        Next
        If oldFileArg IsNot Nothing Then
            If oldFileArg.EndsWith(".old") Then
                Dim t = New Thread(
                    Sub()
                        Try
                            'Give the old version time to shut down
                            Thread.Sleep(1000)
                            File.Delete(oldFileArg)
                        Catch ex As Exception
                            Me.Invoke(Function() MsgBox("Deleting old version failed: " & vbCrLf & ex.Message, MsgBoxStyle.Exclamation))
                        End Try
                    End Sub)
                t.Start()
            Else
                MsgBox("Deleting old version failed: Invalid filename ", MsgBoxStyle.Exclamation)
            End If
        End If




        txtTargetSteamID.SetPlaceholder(txtTargetSteamID.Text)
        txtTargetSteamID.Text = ""
        txtBlockSteamID.SetPlaceholder(txtBlockSteamID.Text)
        txtBlockSteamID.Text = ""

        updateUITimer.Interval = 200
        updateUITimer.Start()
        hotkeyTimer.Interval = 10
        hotkeyTimer.Start()
        updateActiveNodesTimer.Interval = 5000
        updateActiveNodesTimer.Start()
        checkOnHitPacketStorageTimer.Interval = 200
        checkOnHitPacketStorageTimer.Start()
        damageLogTimer.Interval = 1000
        damageLogTimer.Start()
        updateOnlineStateTimer.Interval = Config.OnlineCheckInterval
        updateOnlineStateTimer.Start()
        updateNetNodesTimer.Interval = Config.UpdateNetNodesInterval
        netNodeConnectTimer.Interval = Config.NetNodeConnectInterval
        publishNodesTimer.Interval = Config.PublishNodesInterval
        checkWatchNodeTimer.Interval = Config.CheckWatchNodeInterval

        setupGridViews()

        'Create regkeys if they don't exist
        'Stores steamid as key, current name as value
        My.Computer.Registry.CurrentUser.CreateSubKey("Software\DSCM\BlockedNodes")
        My.Computer.Registry.CurrentUser.CreateSubKey("Software\DSCM\FavoriteNodes")
        My.Computer.Registry.CurrentUser.CreateSubKey("Software\DSCM\RecentNodes")
        My.Computer.Registry.CurrentUser.CreateSubKey("Software\DSCM\Options")

        loadFavoriteNodes()
        loadWhitelistNodes()
        loadBlockedNodes()
        loadRecentNodes()
        loadOptions()
        loadReadme()

        'wait till DSCM is all loaded before we try to attach
        dsAttachmentTimer.Interval = 1000
        dsAttachmentTimer.Start()
        attachDSProcess()

        'Resize window
        chkExpand_CheckedChanged()

        updatecheck()
        updateOnlineState()
    End Sub
    Private Sub setupGridViews()
        Dim AlternateRowColor = Color.FromArgb(&HFFE3E3E3)

        With dgvMPNodes
            .AutoGenerateColumns = False
            .DataSource = activeNodesDisplayList
            .Columns.Add("name", "Name")
            .Columns("name").MinimumWidth = 80
            .Columns("name").AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            .Columns("name").DataPropertyName = "CharacterNameColumn"
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 50
            .Columns("steamId").DataPropertyName = "SteamIdColumn"
            .Columns.Add("soulLevel", "SL")
            .Columns("soulLevel").Width = 60
            .Columns("soulLevel").DataPropertyName = "SoulLevelColumn"
            .Columns("soulLevel").DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
            .Columns.Add("phantomType", "Phantom Type")
            .Columns("phantomType").Width = 80
            .Columns("phantomType").DataPropertyName = "PhantomTypeText"
            .Columns.Add("mpArea", "MP Area")
            .Columns("mpArea").Width = 150
            .Columns("mpArea").DataPropertyName = "MPZoneColumn"
            .Columns.Add("world", "World")
            .Columns("world").Width = 120
            .Columns("world").DataPropertyName = "WorldText"
            .Columns.Add("ping", "Ping")
            .Columns("ping").Width = 75
            .Columns("ping").DataPropertyName = "PingColumn"
            .Columns("ping").DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
            .Font = New Font("Consolas", 10)
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
            .Sort(.Columns("soulLevel"), ListSortDirection.Ascending)
            .Sort(.Columns("mpArea"), ListSortDirection.Ascending)
            .Sort(.Columns("world"), ListSortDirection.Descending)
        End With

        With dgvFavoriteNodes
            .Columns.Add("name", "Name")
            .Columns("name").Width = 180
            .Columns("name").ValueType = GetType(String)
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 145
            .Columns("steamId").ValueType = GetType(String)
            .Columns.Add("isOnline", "O")
            .Columns("isOnline").Width = 20
            .Columns("isOnline").ValueType = GetType(String)
            .Font = New Font("Consolas", 10)
            .AlternatingRowsDefaultCellStyle.BackColor = AlternateRowColor
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
        End With

        With dgvWhitelist
            .Columns.Add("name", "Name")
            .Columns("name").Width = 180
            .Columns("name").ValueType = GetType(String)
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 145
            .Columns("steamId").ValueType = GetType(String)
            .Columns.Add("isOnline", "O")
            .Columns("isOnline").Width = 20
            .Columns("isOnline").ValueType = GetType(String)
            .Font = New Font("Consolas", 10)
            .AlternatingRowsDefaultCellStyle.BackColor = AlternateRowColor
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
        End With

        'Using this as a gui element and the block array. Stores (steam name, steam id)
        With dgvBlockedNodes
            .Columns.Add("name", "Name")
            .Columns("name").Width = 180
            .Columns("name").ValueType = GetType(String)
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 180
            .Columns("steamId").ValueType = GetType(String)
            .Font = New Font("Consolas", 10)
            .AlternatingRowsDefaultCellStyle.BackColor = AlternateRowColor
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
        End With

        'make sure this isn't sortable, since we need it to be ordered chronologically
        With dgvDamageLog
            .Columns.Add("name", "Name")
            .Columns("name").Width = 180
            .Columns("name").ValueType = GetType(String)
            .Columns("name").SortMode = DataGridViewColumnSortMode.NotSortable
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 145
            .Columns("steamId").ValueType = GetType(String)
            .Columns("steamId").SortMode = DataGridViewColumnSortMode.NotSortable
            .Columns.Add("spellAttack", "Spell Sp")
            .Columns("spellAttack").Width = 80
            .Columns("spellAttack").ValueType = GetType(Int32)
            .Columns("spellAttack").SortMode = DataGridViewColumnSortMode.NotSortable
            .Columns.Add("weaponAttack", "Weapon Sp")
            .Columns("weaponAttack").Width = 100
            .Columns("weaponAttack").ValueType = GetType(Int32)
            .Columns("weaponAttack").SortMode = DataGridViewColumnSortMode.NotSortable
            .Font = New Font("Consolas", 10)
            .AlternatingRowsDefaultCellStyle.BackColor = AlternateRowColor
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
        End With
        'pre-populate the rows
        For i As Integer = 0 To DarkSoulsProcess.onHitListTmpStorageSteamIdSize - 1
            dgvDamageLog.Rows.Add()
        Next

        With dgvRecentNodes
            .AutoGenerateColumns = False
            .Columns.Add("name", "Name")
            .Columns("name").Width = 180
            .Columns("name").ValueType = GetType(String)
            .Columns("name").SortMode = DataGridViewColumnSortMode.NotSortable
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 145
            .Columns("steamId").ValueType = GetType(String)
            .Columns("steamId").SortMode = DataGridViewColumnSortMode.NotSortable
            .Columns.Add("orderId", "Order ID")
            .Columns("orderId").Visible = False
            .Columns("orderId").ValueType = GetType(Long)
            .Columns.Add("isOnline", "O")
            .Columns("isOnline").Width = 20
            .Columns("isOnline").ValueType = GetType(String)
            .Columns("isOnline").SortMode = DataGridViewColumnSortMode.NotSortable
            .Font = New Font("Consolas", 10)
            .AlternatingRowsDefaultCellStyle.BackColor = AlternateRowColor
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
        End With

        With dgvDSCMNet
            .AutoGenerateColumns = False
            .DataSource = netNodeDisplayList
            .Columns.Add("name", "Name")
            .Columns("name").MinimumWidth = 80
            .Columns("name").AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            .Columns("name").DataPropertyName = "CharacterNameColumn"
            .Columns.Add("steamId", "Steam ID")
            .Columns("steamId").Width = 145
            .Columns("steamId").DataPropertyName = "SteamIdColumn"
            .Columns("steamId").Visible = False
            .Columns.Add("soulLevel", "SL")
            .Columns("soulLevel").Width = 40
            .Columns("soulLevel").DataPropertyName = "SoulLevelColumn"
            .Columns("soulLevel").DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
            .Columns.Add("phantomType", "Phantom Type")
            .Columns("phantomType").Width = 70
            .Columns("phantomType").DataPropertyName = "PhantomTypeText"
            .Columns.Add("mpArea", "MP Area")
            .Columns("mpArea").Width = 60
            .Columns("mpArea").DataPropertyName = "MPZoneColumn"
            .Columns.Add("world", "World")
            .Columns("world").Width = 195
            .Columns("world").DataPropertyName = "WorldText"
            .Columns.Add("covenant", "Covenant")
            .Columns("covenant").Width = 165
            .Columns("covenant").DataPropertyName = "CovenantColumn"
            .Columns.Add("indictments", "Sin")
            .Columns("indictments").Width = 60
            .Columns("indictments").DataPropertyName = "IndictmentsColumn"
            .Columns("indictments").DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
            .Font = New Font("Consolas", 10)
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
            .Sort(.Columns("steamId"), ListSortDirection.Ascending)
            .Sort(.Columns("soulLevel"), ListSortDirection.Descending)
        End With
    End Sub
    Private Sub loadReadme()
        Dim html As XElement =
            <html>
                <head>
                    <style>
                        body {font-family: Calibri}
                        ol, ul {margin-bottom: 1em}
                        h1 {border-bottom: 1px solid black}
                    </style>
                </head>
                <body>###</body>
            </html>

        Dim htmlString = html.ToString()
        helpView.DocumentText = htmlString.Replace("###", My.Resources.Readme)
        helpView.IsWebBrowserContextMenuEnabled = False
        helpView.AllowWebBrowserDrop = False
    End Sub
    Private Sub helpView_Navigating(sender As System.Object, e As System.Windows.Forms.WebBrowserNavigatingEventArgs) Handles helpView.Navigating
        If e.Url.ToString <> "about:blank" Then
            e.Cancel = True 'Cancel the event to avoid default behavior
            System.Diagnostics.Process.Start(e.Url.ToString()) 'Open the link in the default browser
        End If
    End Sub
    Private Sub loadFavoriteNodes()
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\FavoriteNodes", True)

        For Each id As String In key.GetValueNames()
            dgvFavoriteNodes.Rows.Add(key.GetValue(id), id)
        Next
    End Sub

    Private Sub loadWhitelistNodes()
        dgvWhitelist.Rows.Clear()
        If My.Computer.FileSystem.FileExists(WhitelistLocation) Then
            Dim whitenodes = My.Computer.FileSystem.ReadAllText(WhitelistLocation).Split(New String() {Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries)
            Dim index = 0
            For Each steamid As String In whitenodes
                dgvWhitelist.Rows.Add()
                dgvWhitelist.Rows(index).Cells("steamId").Value = steamid
                Dim str2 As String = Conversions.ToString(Convert.ToInt64(steamid, 16))
                Dim xmlDocument As XmlDocument = New XmlDocument()
                xmlDocument.Load("http://steamcommunity.com/profiles/" + str2 + "?xml=1")
                Dim innerText As String = xmlDocument.SelectSingleNode("/profile/steamID").InnerText
                dgvWhitelist.Rows(index).Cells("name").Value = innerText
                index = index + 1
            Next

        End If
    End Sub

    'Read in blocked node list from register and global block list
    Private Async Sub loadBlockedNodes()
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\BlockedNodes", True)

        Try
            Dim client As New Net.WebClient()
            Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
            Dim content As String = Await client.DownloadStringTaskAsync(Config.GlobalBlocklistUrl)

            'clear existing users marked as from global blocklist
            'this is done so if we remove people from the blocklist they also get removed here
            If key IsNot Nothing Then
                For Each id As String In key.GetValueNames()
                    If id.Contains(BlockTypes.GlobalBlock) Then
                        key.DeleteValue(id)
                    End If
                Next
            End If

            'grab users from global blocklist and save them to the registry
            Dim lines() As String = content.Split({vbCrLf, vbLf}, StringSplitOptions.None)
            For Each line In lines
                If line.Length > 0 Then
                    Dim sublines() As String = line.Split("="c)
                    Dim steamid As String = sublines(0) + BlockTypes.GlobalBlock 'add notation to indicate this is from the global blocklist
                    Dim steamname As String = sublines(1)
                    key.SetValue(CType(steamid, Object), steamname)
                End If
            Next
        Catch ex As Exception
            'Fail silently since nobody wants to be bothered for an blocklist check.
        End Try

        'load blocked users from registry
        If key IsNot Nothing Then
            For Each id As String In key.GetValueNames()
                dgvBlockedNodes.Rows.Add(key.GetValue(id), id)
            Next
        End If

        If dsProcess IsNot Nothing Then
            dsProcess.Sync_MemoryBlockList(dgvBlockedNodes.Rows)
        End If
    End Sub
    Private Sub loadRecentNodes()
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\RecentNodes", True)

        Dim name As String
        Dim tmpRecentID As Long

        For Each id As String In key.GetValueNames()
            name = key.GetValue(id)
            tmpRecentID = name.Split("|")(0)
            name = name.Split("|")(1)
            dgvRecentNodes.Rows.Add(name, id, tmpRecentID)
        Next
    End Sub
    Private Sub loadOptions()
        Dim key As Microsoft.Win32.RegistryKey
        Dim regval As String

        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\Options", True)

        regval = key.GetValue("ExpandDSCM")
        If regval Is Nothing Then key.SetValue("ExpandDSCM", "True")

        regval = key.GetValue("JoinDSCM-Net")
        If regval Is Nothing Then key.SetValue("JoinDSCM-Net", "True")

        regval = key.GetValue("MaxNodes")
        If regval Is Nothing Then key.SetValue("MaxNodes", "20")

        regval = key.GetValue("MinAccountAge")
        If regval Is Nothing Then key.SetValue("MinAccountAge", "True")

        chkExpand.Checked = (key.GetValue("ExpandDSCM") = "True")
        chkDSCMNet.Checked = (key.GetValue("JoinDSCM-Net") = "True")
        nmbMaxNodes.Value = key.GetValue("MaxNodes")
        mandateMinAccountAge.Checked = (key.GetValue("MinAccountAge") = "True")

        optionsLoaded = True
    End Sub
    Private Sub updateOnlineState_Tick() Handles updateOnlineStateTimer.Tick
        updateOnlineState()
    End Sub
    Private Async Sub updateOnlineState()
        Try
            Dim steamIds = New HashSet(Of String)
            For Each Row In dgvRecentNodes.Rows
                steamIds.Add(Row.Cells("steamId").Value)
            Next
            For Each Row In dgvFavoriteNodes.Rows
                If steamIds.Count < 100 Then steamIds.Add(Row.Cells("steamId").Value)
            Next
            Dim converter As New Converter(Of String, String)(Function(num) Convert.ToInt64(num, 16).ToString())
            Dim idQuery = String.Join(",", Array.ConvertAll(steamIds.ToArray(), converter))
            Dim uri = Config.OnlineCheckUrl & "?ids=" & idQuery
            Dim client As New Net.WebClient()
            Dim contents() As Byte = Await client.DownloadDataTaskAsync(uri)

            Dim onlineInfo = New Dictionary(Of Int64, Boolean)
            Try
                Dim parser As New FileIO.TextFieldParser(New MemoryStream(contents))
                parser.SetDelimiters({","})

                While Not parser.EndOfData
                    Dim strings = parser.ReadFields()
                    onlineInfo(Int64.Parse(strings(0))) = Boolean.Parse(strings(1))
                End While
            Catch
                Return
            End Try
            For Each Row In dgvRecentNodes.Rows
                Try
                    If onlineInfo(converter(Row.Cells("steamId").Value())) Then
                        Row.Cells("isOnline").Value = "Y"
                    Else
                        Row.Cells("isOnline").Value = "N"
                    End If
                Catch ex As KeyNotFoundException
                End Try
            Next
            For Each Row In dgvFavoriteNodes.Rows
                Try
                    If onlineInfo(converter(Row.Cells("steamId").Value())) Then
                        Row.Cells("isOnline").Value = "Y"
                    Else
                        Row.Cells("isOnline").Value = "N"
                    End If
                Catch ex As KeyNotFoundException
                End Try
            Next
            For Each Row In dgvWhitelist.Rows
                Try
                    If onlineInfo(converter(Row.Cells("steamId").Value())) Then
                        Row.Cells("isOnline").Value = "Y"
                    Else
                        Row.Cells("isOnline").Value = "N"
                    End If
                Catch ex As KeyNotFoundException
                End Try
            Next
        Catch ex As Exception
            'Fail silently since nobody wants to be bothered for the online check.
        End Try
    End Sub
    Private Async Sub updatecheck()
        Try
            Dim client As New Net.WebClient()
            Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
            Dim content As String = Await client.DownloadStringTaskAsync(Config.VersionCheckUrl)

            Dim lines() As String = content.Split({vbCrLf, vbLf}, StringSplitOptions.None)
            Dim stableVersion = lines(0)
            Dim stableUrl = lines(2)
            Dim testVersion = lines(1)
            Dim testUrl = lines(3)

            If stableVersion > Version.Replace(".", "") Then
                lblNewVersion.Visible = True
                btnUpdate.Visible = True
                btnUpdate.Tag = stableUrl
                lblNewVersion.Text = "New stable version available"
            ElseIf testVersion > Version.Replace(".", "") Then
                lblNewVersion.Visible = True
                btnUpdate.Visible = True
                btnUpdate.Tag = testUrl
                lblNewVersion.Text = "New testing version available"
            End If
        Catch ex As Exception
            'Fail silently since nobody wants to be bothered for an update check.
        End Try
    End Sub
    Private Sub btnUpdate_Click(sender As Button, e As EventArgs) Handles btnUpdate.Click
        Dim updateWindow As New UpdateWindow(sender.Tag)
        updateWindow.ShowDialog()
        If updateWindow.WasSuccessful Then
            If dsProcess IsNot Nothing Then
                dsProcess.Dispose()
                dsProcess = Nothing
            End If
            Process.Start(updateWindow.NewAssembly, """--old-file=" & updateWindow.OldAssembly & """")
            Me.Close()
        End If
    End Sub
    Private Sub connectToNetNode() Handles netNodeConnectTimer.Tick
        If _netClient Is Nothing OrElse dsProcess Is Nothing Then Return

        Dim inMenu = (dsProcess.SelfSteamId = "" OrElse
                dsProcess.SelfNode.CharacterName = "" OrElse
                dsProcess.SelfNode.PhantomType = -1)

        If inMenu AndAlso dsProcess.NodeCount >= Config.BadNodesThreshold Then
            Return
        End If

        If dsProcess.NodeCount < dsProcess.MaxNodes - Config.NodesReservedForSteam - 1 Then
            Dim candidate As DSNode = selectNetNodeForConnecting()
            If candidate IsNot Nothing Then
                connectToSteamId(candidate.SteamId)
            End If
        End If
    End Sub
    Private Function selectNetNodeForConnecting() As DSNode
        Dim blackSet As New HashSet(Of String)()
        blackSet.Add(dsProcess.SelfNode.SteamId)
        For Each c In recentConnections
            blackSet.Add(c.Item2)
        Next
        For Each n In dsProcess.ConnectedNodes.Values
            blackSet.Add(n.SteamId)
        Next
        'Don't attempt connections with blocked nodes
        For Each n In dgvBlockedNodes.Rows
            blackSet.Add(n.Cells("steamId").Value)
        Next

        Dim candidates As New List(Of DSNode)
        For Each node In _netClient.netNodes.Values
            If blackSet.Contains(node.SteamId) Then Continue For
            candidates.Add(node)
        Next

        If candidates.Count = 0 Then Return Nothing

        'Pick the first few nodes at random to improve the network structure
        If dsProcess.NodeCount < Config.BadNodesThreshold Then
            Dim idx = random.Next(candidates.Count)
            Return candidates.Item(idx)
        End If

        Dim self = dsProcess.SelfNode

        'These read out dsProcess memory, so don't calculate them for every node
        Dim anorLondoInvading = self.Covenant = Covenant.DarkmoonBlade AndAlso dsProcess.HasDarkmoonRingEquipped
        Dim forestInvading = self.Covenant = Covenant.ForestHunter AndAlso dsProcess.HasCatCovenantRingEquipped
        Dim sorted As IOrderedEnumerable(Of DSNode) = candidates _
            .OrderByDescending(Function(other) (other.World <> "-1--1")) _
            .ThenByDescending(Function(other) As Boolean
                                  'Special cross-world invasions
                                  If anorLondoInvading Then
                                      'TODO: Use others dark anor londo info once we have it
                                      If other.World = AnorLondoWorld Then
                                          Return self.canDarkmoonInvade(other)
                                      End If
                                  ElseIf forestInvading Then
                                      If DarkrootGardenZones.Contains(other.MPZone) Then
                                          If other.HasExtendedInfo And other.Covenant = Covenant.ForestHunter Then
                                              Return False
                                          Else
                                              Return self.canForestInvade(other)
                                          End If
                                      End If
                                  End If
                                  Return False
                              End Function) _
            .ThenByDescending(Function(other) (other.World = self.World))

        Dim pvpSorting = Function(s As IOrderedEnumerable(Of DSNode))
                             Return s.ThenByDescending(Function(other) self.canBeRedSignSummoned(other)) _
                                     .ThenByDescending(Function(other) other.canBeRedSignSummoned(self)) _
                                     .ThenByDescending(Function(other) other.canRedEyeInvade(self)) _
                                     .ThenByDescending(Function(other) self.canRedEyeInvade(other)) _
                                     .ThenByDescending(Function(other) other.canBeSummoned(self)) _
                                     .ThenByDescending(Function(other) self.canBeSummoned(other))
                         End Function

        Dim sameMpZone = Function(other) other.MPZone = self.MPZone

        If self.Covenant = Covenant.Darkwraith Then
            sorted = sorted.ThenByDescending(Function(other) self.canRedEyeInvade(other)) _
                .ThenByDescending(sameMpZone) _
                .ThenByDescending(Function(other) other.canBeRedSignSummoned(self))
        ElseIf self.Covenant = Covenant.DarkmoonBlade Then
            sorted = sorted.ThenByDescending(Function(other) self.canDarkmoonInvade(other)) _
                .ThenByDescending(Function(other)
                                      If other.HasExtendedInfo Then
                                          Return If(other.Indictments > 0, 1, -1)
                                      Else
                                          Return 0
                                      End If
                                  End Function) _
                .ThenByDescending(sameMpZone)
            sorted = pvpSorting(sorted)
        ElseIf self.Covenant = Covenant.ForestHunter Or self.Covenant = Covenant.ChaosServant Then
            'Nothing specific to be done here, assume general interest in PVP
            sorted = pvpSorting(sorted)
        ElseIf self.Covenant = Covenant.GravelordServant Or self.Covenant = Covenant.PathOfTheDragon Then
            sorted = sorted.ThenByDescending(Function(other) self.canBeSummoned(other)) _
                .ThenByDescending(sameMpZone)
            sorted = pvpSorting(sorted)
        ElseIf self.Covenant = Covenant.WarriorOfSunlight Then
            sorted = sorted.ThenByDescending(Function(other) self.canBeSummoned(other)) _
                .ThenByDescending(Function(other) other.canBeSummoned(self))
        Else
            'No Covenant, Way of White, Princess Guard
            sorted = sorted.ThenByDescending(Function(other) self.canBeSummoned(other)) _
                .ThenByDescending(Function(other) other.canBeSummoned(self))
        End If

        sorted = sorted.ThenByDescending(sameMpZone) _
            .ThenBy(Function(other) Math.Abs(other.SoulLevel - self.SoulLevel))
        Return sorted(0)
    End Function
    Private Function nodeRanking(other As DSNode) As Integer
        '0 = good, 1 = half-bad, 2 = bad
        'Half-Bad = I can't interact with them, but they can invade me
        Dim self = dsProcess.SelfNode
        If (self.Covenant = Covenant.DarkmoonBlade AndAlso other.World = AnorLondoWorld AndAlso
            self.canDarkmoonInvade(other) AndAlso dsProcess.HasDarkmoonRingEquipped) Then Return 0
        If (self.Covenant = Covenant.ForestHunter AndAlso DarkrootGardenZones.Contains(other.MPZone) AndAlso
            self.canForestInvade(other) AndAlso dsProcess.HasCatCovenantRingEquipped) Then Return 0

        If self.World = other.World Then
            Dim coopPossible = (self.canBeSummoned(other) OrElse other.canBeSummoned(self))
            If coopPossible Then Return 0
            If self.Covenant = Covenant.Darkwraith And self.canRedEyeInvade(other) Then Return 0
            If self.Covenant = Covenant.DarkmoonBlade And self.canDarkmoonInvade(other) Then Return 0

            If self.Indictments > 0 And other.canDarkmoonInvade(self) Then Return 1
            If other.canRedEyeInvade(self) Then Return 1
        End If

        'TODO: check whether Sif is alive
        'If we knew that the other player is a Forest Hunter, we could mark this as a good node
        If (self.Covenant <> Covenant.ForestHunter AndAlso DarkrootGardenZones.Contains(self.MPZone) AndAlso
            other.canForestInvade(self)) Then Return 1
        'TODO: Add Dark Anor Londo check once we read out anor londo darkness
        Return 2
    End Function
    Private Sub handleDisconnects()
        If _netClient Is Nothing Or dsProcess Is Nothing Then Return
        If dsProcess.SelfNode.PhantomType = -1 Then Return

        Dim now As Date = Date.UtcNow
        Dim disconnectCandidates As New List(Of Tuple(Of ConnectedNode, Integer))()
        Dim badNodeCount = 0
        For Each connectedNode In connectedNodes.Values
            If Not IsNothing(watchSteamId) AndAlso connectedNode.node.SteamId = watchSteamId Then
                Continue For
            End If
            Dim ranking = nodeRanking(connectedNode.node)
            If ranking = 0 Then
                connectedNode.lastGoodTime = now
            Else
                badNodeCount += 1
                Dim badSeconds = (now - connectedNode.lastGoodTime).TotalSeconds
                If (manualConnections.Contains(connectedNode.node.SteamId) And badSeconds < Config.ManualNodeGracePeriod) Then
                    Continue For
                ElseIf ranking = 1 And badSeconds < Config.HalfBadNodeGracePeriod Then
                    Continue For
                ElseIf ranking = 2 And badSeconds < Config.BadNodeGracePeriod Then
                    Continue For
                End If
                'We might currently have an online interaction
                If (dsProcess.SelfNode.PhantomType = PhantomType.Coop Or dsProcess.SelfNode.PhantomType = PhantomType.Invader Or
                    connectedNode.node.PhantomType = PhantomType.Coop Or connectedNode.node.PhantomType = PhantomType.Invader) Then
                    Continue For
                End If
                disconnectCandidates.Add(Tuple.Create(connectedNode, ranking))
            End If
        Next

        Dim disconnectCount = Math.Min(badNodeCount - Config.BadNodesThreshold, disconnectCandidates.Count)
        disconnectCount = Math.Min(DisconnectTargetFreeNodes - (nmbMaxNodes.Value - dsProcess.NodeCount), disconnectCount)

        If disconnectCount < 1 Then
            Return
        End If
        Dim disconnectNodes = disconnectCandidates _
                .OrderByDescending(Function(x) x.Item2) _
                .Take(disconnectCount)
        For Each disconnectNode In disconnectNodes
            dsProcess.DisconnectSteamId(disconnectNode.Item1.node.SteamId)
        Next
    End Sub
    Private Sub updateUI() Handles updateUITimer.Tick
        If dsProcess Is Nothing Then
            btnLaunchDS.Visible = False
        Else
            'Node display
            'Changes the comparison instruction to display it if value is 0, rather than changing the value itself
            chkDebugDrawing.Checked = dsProcess.DrawNodes

            btnLaunchDS.Visible = False

            Dim maxNodes = dsProcess.MaxNodes
            ' Reading the value messes with input
            If Not nmbMaxNodes.Focused Then
                If maxNodes <> nmbMaxNodes.Value And maxNodes >= nmbMaxNodes.Minimum And maxNodes <= nmbMaxNodes.Maximum Then
                    dsProcess.MaxNodes = nmbMaxNodes.Value
                    'Read again, in case something else is force-setting it
                    maxNodes = dsProcess.MaxNodes
                End If
            End If
            If maxNodes >= nmbMaxNodes.Minimum And maxNodes <= nmbMaxNodes.Maximum Then
                If Not nmbMaxNodes.Focused Then
                    nmbMaxNodes.Value = maxNodes
                End If
                nmbMaxNodes.Enabled = True
                nmbMaxNodes.BackColor = New Color()
            Else
                nmbMaxNodes.Enabled = False
                nmbMaxNodes.BackColor = System.Drawing.Color.FromArgb(255, 200, 200)
            End If

            'Don't update the text box if it's clicked in, so people can copy/paste without losing cursor.
            'Probably don't need to update this more than once anyway, but why not?
            If Not txtSelfSteamID.Focused Then
                txtSelfSteamID.Text = dsProcess.SelfSteamId
            End If

            txtCurrNodes.Text = dsProcess.NodeCount

            'errorCheckSteamName()
            txtLocalSteamName.Text = dsProcess.SelfSteamName
            If txtLocalSteamName.Text.Length > 15 Then
                txtLocalSteamName.BackColor = Color.OrangeRed
            Else
                txtLocalSteamName.BackColor = DefaultBackColor
            End If



            txtWatchdogActive.Text = dsProcess.HasWatchdog
            txtSin.Text = dsProcess.Sin
            txtDeaths.Text = dsProcess.Deaths
            txtPhantomType.Text = dsProcess.PhantomType
            txtTeamType.Text = dsProcess.TeamType
            txtClearCount.Text = dsProcess.ClearCount
            txtTimePlayed.Text = TimeSpan.FromMilliseconds(dsProcess.TimePlayed).ToString("ddd\.hh\:mm\:ss")

            txtRedCooldown.Text = Math.Round(dsProcess.redCooldown, 0)
            txtBlueCooldown.Text = Math.Round(dsProcess.blueCooldown, 0)


            txtXPos.Text = Math.Round(dsProcess.xPos, 1)
            txtYPos.Text = Math.Round(dsProcess.yPos, 1)
            txtZPos.Text = Math.Round(dsProcess.zPos, 1)

            Dim flaglist As New NameValueCollection

            flaglist.Add("Gaping Dragon", dsProcess.FlagsGapingDragonDead)
            flaglist.Add("Bell Gargoyles", dsProcess.FlagsBellGargoylesDead)
            flaglist.Add("Priscilla", dsProcess.FlagsPriscillaDead)
            flaglist.Add("Sif", dsProcess.FlagsSifDead)
            flaglist.Add("Pinwheel", dsProcess.FlagsPinwheelDead)
            flaglist.Add("Nito", dsProcess.FlagsNitoDead)
            flaglist.Add("Chaos Witch Quelaag", dsProcess.FlagsQuelaagDead)
            flaglist.Add("Bed of Chaos", dsProcess.FlagsBedOfChaosDead)
            flaglist.Add("Iron Golem", dsProcess.FlagsIronGolemDead)
            flaglist.Add("Ornstein & Smough", dsProcess.FlagsOnSDead)
            flaglist.Add("Four Kings", dsProcess.FlagsFourKingsDead)
            flaglist.Add("Seath", dsProcess.FlagsSeathDead)
            flaglist.Add("Gwyn", dsProcess.FlagsGwynDead)
            flaglist.Add("Taurus Demon", dsProcess.FlagsTaurusDead)
            flaglist.Add("Capra Demon", dsProcess.FlagsCapraDead)
            flaglist.Add("Moonlight Butterfly", dsProcess.FlagsMoonlightButterflyDead)
            flaglist.Add("Sanctuary Guardian", dsProcess.FlagsSanctuaryGuardianDead)
            flaglist.Add("Artorias", dsProcess.FlagsArtoriasDead)
            flaglist.Add("Manus", dsProcess.FlagsManusDead)
            flaglist.Add("Kalameet", dsProcess.FlagsKalameetDead)
            flaglist.Add("Demon Firesage", dsProcess.FlagsDemonFiresageDead)
            flaglist.Add("Ceaseless Discharge", dsProcess.FlagsCeaselessDischargeDead)
            flaglist.Add("Centipede Demon", dsProcess.FlagsCentipedeDemonDead)
            flaglist.Add("Gwyndolin", dsProcess.FlagsGwyndolinDead)
            flaglist.Add("Dark Anor Londo", dsProcess.FlagsDarkAnorLondo)
            flaglist.Add("New Londo Drained", dsProcess.FlagsNewLondoDrained)

            For Each item In flaglist.Keys
                Try
                    clbEventFlags.SetItemChecked(clbEventFlags.Items.IndexOf(item), flaglist.GetValues(item)(0))
                Catch ex As Exception
                    MsgBox("Failed flag lookup - " & ex.Message)
                End Try
            Next

            UpdateDebugLog()
        End If


        If Not tabDSCMNet.Text = "DSCM-Net (" & dgvDSCMNet.Rows.Count & ")" Then
            tabDSCMNet.Text = "DSCM-Net (" & dgvDSCMNet.Rows.Count & ")"
        End If
    End Sub
    Private Sub UpdateDebugLog()
        If Not IsNothing(dsProcess) Then
            lbxDebugLog.UpdateFromDS(dsProcess.debugLog)
        End If
    End Sub
    Private Sub chkLoggerEnabled_CheckedChanged(sender As Object, e As EventArgs) Handles chkLoggerEnabled.CheckedChanged
        If Not IsNothing(dsProcess) Then dsProcess.enableDebugLog = chkLoggerEnabled.Checked
    End Sub
    Private Async Sub checkWatchNode() Handles checkWatchNodeTimer.Tick
        If _netClient Is Nothing Or dsProcess Is Nothing Then Return

        Dim connectedToOldNode = Not IsNothing(watchSteamId) AndAlso connectedNodes.ContainsKey(watchSteamId)

        If connectedToOldNode And (DateTime.UtcNow - watchExchangedLastTime).TotalMilliseconds <= Config.ExchangeWatchNodeInterval Then
            Return
        End If

        If dsProcess.NodeCount - connectedToOldNode < dsProcess.MaxNodes - Config.NodesReservedForSteam Then
            If Not IsNothing(watchSteamId) Then
                dsProcess.DisconnectSteamId(watchSteamId)
            End If

            Try
                Dim newWatchId = Await _netClient.getWatchId()
                watchSteamId = newWatchId
                watchExchangedLastTime = DateTime.UtcNow
                dsProcess.ConnectToSteamId(newWatchId)
            Catch ex As Exception
                txtIRCDebug.Text = "Failed to get new watch: " & ex.Message
            End Try
        End If
    End Sub
    Private Async Sub updateNetNodes() Handles updateNetNodesTimer.Tick
        If _netClient IsNot Nothing Then
            Await _netClient.loadNodes()
            netNodeDisplayList.SyncWithDict(_netClient.netNodes, dgvDSCMNet)
        End If
    End Sub
    Private Async Sub publishNodes() Handles publishNodesTimer.Tick
        If _netClient IsNot Nothing AndAlso dsProcess IsNot Nothing AndAlso dsProcess.SelfNode.SteamId IsNot Nothing Then
            Await _netClient.publishLocalNodes(dsProcess.SelfNode, dsProcess.ConnectedNodes.Values(), dsProcess.ReadLobbyList())
        End If
    End Sub
    Private Shared Sub hotkeyTimer_Tick() Handles hotkeyTimer.Tick
        Dim ctrlkey As Boolean
        Dim oneKey As Boolean 'Toggle Node Display
        Dim twoKey As Boolean 'Previously toggled NamedNodes, now a free hotkey.

        ctrlkey = GetAsyncKeyState(Keys.ControlKey)
        oneKey = GetAsyncKeyState(Keys.D1)
        twoKey = GetAsyncKeyState(Keys.D2)

        If (ctrlkey And oneKey) And Not (MainWindow.ctrlHeld And MainWindow.oneHeld) Then
            MainWindow.chkDebugDrawing.Checked = Not MainWindow.chkDebugDrawing.Checked
        End If


        If (ctrlkey And twoKey) And Not (MainWindow.ctrlHeld And MainWindow.twoheld) Then
            'Hotkey available
        End If

        MainWindow.ctrlHeld = ctrlkey
        MainWindow.oneHeld = oneKey
        MainWindow.twoheld = twoKey
    End Sub
    Private Sub attachDSProcess() Handles dsAttachmentTimer.Tick
        If dsProcess IsNot Nothing Then
            If Not dsProcess.IsAttached Then
                dsProcess.Dispose()
                dsProcess = Nothing
            End If
        End If
        If dsProcess Is Nothing Then
            Try
                whitelist.Checked = False
                dsProcess = New DarkSoulsProcess()
                dsProcessStatus.Text = " Attached to Dark Souls process"
                dsProcessStatus.BackColor = System.Drawing.Color.FromArgb(200, 255, 200)
                dsProcess.enableDebugLog = chkLoggerEnabled.Checked
            Catch ex As DSProcessAttachException
                dsProcessStatus.Text = " " & ex.Message
                dsProcessStatus.BackColor = System.Drawing.Color.FromArgb(255, 200, 200)
            End Try
        End If
    End Sub

    Private Sub chkDebugDrawing_CheckedChanged(sender As Object, e As EventArgs) Handles chkDebugDrawing.CheckedChanged
        If IsNothing(dsProcess) Then
            chkDebugDrawing.Checked = False
            Exit Sub
        End If
        dsProcess.DrawNodes = chkDebugDrawing.Checked
    End Sub

    Private Sub whitelist_CheckedChanged(sender As Object, e As EventArgs) Handles whitelist.CheckedChanged
        If IsNothing(dsProcess) Then
            whitelist.Checked = False
            Exit Sub
        End If
        If whitelist.Checked = True Then
            If My.Computer.FileSystem.FileExists(WhitelistLocation) Then
                Try
                    'Read in the whitelist file
                    Dim whitenodes = My.Computer.FileSystem.ReadAllText(WhitelistLocation).Split(New String() {Environment.NewLine}, StringSplitOptions.None)
                    If whitenodes.Length < 1 Then Return
                    'Write it to the whitelist in-memory array and sync
                    dsProcess.Sync_MemoryWhiteList(whitenodes)
                    'set the listType to Whitelist
                    dsProcess.WriteUInt32(dsProcess.listTypeInMemory, 1)
                Catch ex As Exception
                    Dim thread2_whitelist As New Thread(
                      Sub()
                          MsgBox("Malformed whitelist.txt", MsgBoxStyle.Information)
                      End Sub
                    )
                    thread2_whitelist.Start()
                    whitelist.Checked = False
                End Try
            Else
                'Warning about failure to find file
                Dim thread1_whitelist As New Thread(
                  Sub()
                      MsgBox("Unable to find whitelist.txt (Nobody is on your whitelist)", MsgBoxStyle.Information)
                  End Sub
                )
                thread1_whitelist.Start()
                whitelist.Checked = False
            End If
        Else
            'set the listType back to Blocklist
            dsProcess.WriteUInt32(dsProcess.listTypeInMemory, 0)
        End If
    End Sub

    Private Sub errorCheckSteamName()
        'Disabled temporarily due to being non-functional
        Dim byt() As Byte
        byt = Encoding.Unicode.GetBytes(dsProcess.SelfSteamName)

        If byt.Length > &H1D Then ReDim Preserve byt(&H1D)

        Dim tmpStr As String
        tmpStr = Encoding.Unicode.GetString(byt)
        tmpStr = tmpStr.Replace("#", "")

        If byt(0) = 0 Then tmpStr = "Invalid Name"

        dsProcess.SelfSteamName = tmpStr
    End Sub

    Private Sub checkOnHitPacketStorage() Handles checkOnHitPacketStorageTimer.Tick
        If dsProcess IsNot Nothing Then
            'VS doesn't have short circut eval lmao
            If dsProcess.type18TmpStorageSteamId IsNot Nothing Then
                'grab the steam id and unset it
                Dim steamid_int = dsProcess.ReadUInt64(dsProcess.type18TmpStorageSteamId)
                Dim steamid = steamid_int.ToString("x16")

                If steamid_int <> 0 Then
                    dsProcess.WriteBytes(dsProcess.type18TmpStorageSteamId, {0, 0, 0, 0, 0, 0, 0, 0})
                    'since we're grabbing this asynchronously, make sure we didn't already just block them
                    If Not isUserBlocked(steamid) Then
                        blockUser(steamid, BlockTypes.OnHitHackBlock)
                    End If
                End If
            End If
        End If
    End Sub

    Private Sub damageLog_Sync() Handles damageLogTimer.Tick
        If dsProcess IsNot Nothing Then
            If dsProcess.onHitListTmpStorageSteamId IsNot Nothing Then
                Dim damageLog_data = dsProcess.ReadBytes(dsProcess.onHitListTmpStorageSteamId, DarkSoulsProcess.onHitListTmpStorageSteamIdSize * (8 + 4 + 4))

                'fill in the ui with the grabbed data
                For i As Integer = 0 To DarkSoulsProcess.onHitListTmpStorageSteamIdSize - 1
                    Dim steamid = BitConverter.ToUInt64(damageLog_data, (i * (8 + 4 + 4)))
                    Dim steamid_str As String = steamid.ToString("x16")
                    Dim spellSpeffect = BitConverter.ToInt32(damageLog_data, (i * (8 + 4 + 4)) + 8)
                    Dim weaponSpeffect = BitConverter.ToInt32(damageLog_data, (i * (8 + 4 + 4)) + 8 + 4)

                    'use the connected list to grab the name
                    If steamid <> 0 And dsProcess.ConnectedNodes.ContainsKey(steamid_str) Then
                        dgvDamageLog.Rows(i).Cells("name").Value = dsProcess.ConnectedNodes(steamid_str).CharacterName
                    End If
                    dgvDamageLog.Rows(i).Cells("steamId").Value = steamid_str
                    dgvDamageLog.Rows(i).Cells("spellAttack").Value = spellSpeffect
                    dgvDamageLog.Rows(i).Cells("weaponAttack").Value = weaponSpeffect
                Next
            End If
        End If
    End Sub

    Private Sub updateActiveNodes() Handles updateActiveNodesTimer.Tick
        Dim selfNode As DSNode = Nothing
        If dsProcess IsNot Nothing Then
            dsProcess.UpdateNodes()
            If dsProcess.SelfNode.SteamId Is Nothing Then Return
            For Each kv In dsProcess.ConnectedNodes
                If connectedNodes.ContainsKey(kv.Key) Then
                    connectedNodes(kv.Key).node = kv.Value.Clone()
                Else
                    connectedNodes(kv.Key) = New ConnectedNode(kv.Value.Clone())
                End If
            Next
            For Each steamId In connectedNodes.Keys.ToList()
                If Not dsProcess.ConnectedNodes.ContainsKey(steamId) Then
                    connectedNodes.Remove(steamId)
                End If
            Next
            selfNode = dsProcess.SelfNode.Clone()
        Else
            connectedNodes.Clear()

        End If

        Dim activeNodes = connectedNodes.ToDictionary(Function(kv) kv.Key, Function(kv) kv.Value.node)
        If selfNode IsNot Nothing Then
            activeNodes.Add(selfNode.SteamId, selfNode)
        End If
        activeNodesDisplayList.SyncWithDict(activeNodes)

        'Color Rows according to ranking
        For Each row As DataGridViewRow In dgvMPNodes.Rows
            Dim steamId = row.Cells("steamId").Value

            row.DefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(0, 0, 0)
            If steamId = selfNode.SteamId Then
                row.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(198, 239, 206)
            ElseIf steamId = watchSteamId Then
                row.DefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(100, 100, 100)
                row.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(200, 200, 200)
            Else
                Dim ranking = nodeRanking(activeNodes(steamId))
                If ranking = 2 Then
                    row.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(255, 199, 206)
                ElseIf ranking = 1 Then
                    row.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(255, 235, 156)
                Else
                    row.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(255, 255, 255)
                End If
            End If
            If steamId = selfNode.SteamId Or manualConnections.Contains(steamId) Then
                row.DefaultCellStyle.Font = New Font(dgvMPNodes.DefaultCellStyle.Font, FontStyle.Bold)
            Else
                row.DefaultCellStyle.Font = Nothing
            End If
        Next

        updateRecentNodes()
        'Do this now as our node info as recent as possible
        handleDisconnects()
    End Sub
    Private Sub updateRecentNodes()
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\RecentNodes", True)

        Dim recentNodeDict As New Dictionary(Of String, DataGridViewRow)
        For Each row In dgvRecentNodes.Rows
            recentNodeDict.Add(row.Cells("steamId").Value, row)
        Next

        Dim currentTime As Long = (DateTime.UtcNow - New DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds
        For Each node In activeNodesDisplayList
            If node.SteamId <> txtSelfSteamID.Text Then
                If Not recentNodeDict.ContainsKey(node.SteamId) Then
                    dgvRecentNodes.Rows.Add(node.CharacterName, node.SteamId, currentTime, "Y")
                Else
                    recentNodeDict(node.SteamId).Cells("orderId").Value = currentTime
                End If
            End If
            key.SetValue(node.SteamId, currentTime.ToString() & "|" & node.CharacterName)
        Next

        'Limit recent nodes to 70
        If dgvRecentNodes.Rows.Count > 70 Then
            Dim recentNodes As New List(Of DataGridViewRow)
            For Each row In dgvRecentNodes.Rows
                recentNodes.Add(row)
            Next

            recentNodes = recentNodes.OrderBy(Function(row) CType(row.Cells("orderId").Value, Long)).ToList()
            For i = 0 To dgvRecentNodes.Rows.Count - 70
                Dim id As String = recentNodes(i).Cells(1).Value
                dgvRecentNodes.Rows.Remove(recentNodes(i))

                If Not key.GetValue(id) Is Nothing Then
                    key.DeleteValue(id)
                End If
            Next
        End If
    End Sub
    Private Sub chkExpand_CheckedChanged() Handles chkExpand.CheckedChanged
        Dim key As Microsoft.Win32.RegistryKey

        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\Options", True)
        key.SetValue("ExpandDSCM", chkExpand.Checked)

        If chkExpand.Checked Then
            Me.Width = 800
            Me.Height = 680
            tabs.Visible = True
            btnAddFavorite.Visible = True
            btnRemFavorite.Visible = True
            btnRemBlock.Visible = True
            btnAddBlock.Visible = True
            Me.txtBlockSteamID.Location = New System.Drawing.Point(291, 64)
            Me.txtBlockSteamID.Size = New System.Drawing.Size(243, 20)

            Me.blockUserId.Location = New System.Drawing.Point(291, 85)
            Me.blockUserId.Size = New System.Drawing.Size(243, 23)

            Me.txtTargetSteamID.Location = New System.Drawing.Point(534, 64)
            Me.txtTargetSteamID.Size = New System.Drawing.Size(243, 20)

            Me.btnAttemptId.Location = New System.Drawing.Point(534, 85)
            Me.btnAttemptId.Size = New System.Drawing.Size(243, 23)

        Else
            Me.Width = 500
            Me.Height = 190
            tabs.Visible = False
            btnAddFavorite.Visible = False
            btnRemFavorite.Visible = False
            btnRemBlock.Visible = False
            btnAddBlock.Visible = False
            Me.txtBlockSteamID.Location = New System.Drawing.Point(10, 74)
            Me.txtBlockSteamID.Size = New System.Drawing.Size(230, 20)

            Me.blockUserId.Location = New System.Drawing.Point(10, 95)
            Me.blockUserId.Size = New System.Drawing.Size(230, 23)

            Me.txtTargetSteamID.Location = New System.Drawing.Point(250, 74)
            Me.txtTargetSteamID.Size = New System.Drawing.Size(230, 20)

            Me.btnAttemptId.Location = New System.Drawing.Point(250, 95)
            Me.btnAttemptId.Size = New System.Drawing.Size(230, 23)
        End If
    End Sub
    Private Sub nmbMaxNodes_ValueChanged(sender As Object, e As EventArgs) Handles nmbMaxNodes.ValueChanged
        If Not IsNothing(dsProcess) Then
            dsProcess.MaxNodes = nmbMaxNodes.Value
        End If
        If optionsLoaded Then
            Dim key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\Options", True)
            key.SetValue("MaxNodes", nmbMaxNodes.Value)
        End If
    End Sub
    Private Sub connectToSteamId(steamId As String)
        If dsProcess IsNot Nothing Then
            Try
                dsProcess.ConnectToSteamId(steamId)
            Catch ex As DSConnectException
                dsProcessStatus.Text = " Connect failed: " & ex.Message
                dsProcessStatus.BackColor = System.Drawing.Color.FromArgb(255, 153, 51)
                Return
            End Try

            Dim now As Date = DateTime.UtcNow
            recentConnections.Enqueue(Tuple.Create(now, steamId))
            While (now - recentConnections.Peek().Item1).TotalSeconds > Config.ConnectionRetryTimeout
                recentConnections.Dequeue()
            End While
        End If
    End Sub

    'Check if text proved in textbox matches a valid steam id. Return it as string if so
    Function verifySteamId(inputBox As TextBox) As String
        If String.IsNullOrWhiteSpace(inputBox.Text) Then
            MsgBox("No target for connection given", MsgBoxStyle.Critical)
            Return Nothing
        End If
        Dim idString As String = inputBox.Text.Replace(" ", "")

        If Not Regex.IsMatch(idString, "^\d+$") Then
            Dim m As Match = Regex.Match(idString, "https?://steamcommunity.com/profiles/(7\d+)")
            If m.Success Then
                'The url contains the steamid, no need for a network request
                idString = m.Groups.Item(1).Value
            ElseIf Regex.IsMatch(idString, "^https?://steamcommunity.com/") Then
                'Get the steamid via api request
                Try
                    Dim url As String = idString.Split("?")(0) & "?xml=1"
                    Dim document As New Xml.XmlDocument()
                    document.Load(url)
                    Dim idNode = document.SelectSingleNode("/profile/steamID64")
                    idString = idNode.InnerText
                Catch ex As Exception
                    'We display an error message later on
                End Try
            End If
        End If

        If idString(0) = "7" Then
            'If it starts with a 7, assume it's the Steam64 ID in int64 form.
            Try
                Dim steamIdInt As Int64 = idString
                idString = "0" & Hex(steamIdInt).ToLower
            Catch ex As InvalidCastException
                'We display an error message later on
            End Try
        End If
        Dim validTarget As Boolean = False
        If idString.Length = 16 Then
            Try
                Convert.ToInt64(idString, 16)
                validTarget = True
            Catch ex As Exception
            End Try
        End If
        If Not validTarget Then
            MsgBox("The given target could not be converted to a Steam64 ID:" & vbCrLf & inputBox.Text, MsgBoxStyle.Critical)
            Return Nothing
        End If
        If dsProcess Is Nothing Then
            MsgBox("You can only connect to other players while Dark Souls is running.", MsgBoxStyle.Critical)
            Return Nothing
        End If

        Return idString
    End Function

    Private Sub btnAttemptId_MouseClick(sender As Object, e As EventArgs) Handles btnAttemptId.Click
        Dim idString As String = verifySteamId(txtTargetSteamID)
        If idString IsNot Nothing Then
            manualConnections.Add(idString)
            connectToSteamId(idString)
        End If
    End Sub

    'Blocked the given user id if possible
    'Adds to registery and the block list
    Private Sub blockUser(idString As String, blockType As String)
        idString = idString.ToLower()
        Dim idStringNoted = idString + blockType

        'allow friends to cheat on each other
        For Each Row In dgvFavoriteNodes.Rows
            If Row.Cells("steamId").Value = idString Then
                Return
            End If
        Next

        If dgvBlockedNodes.Rows.Count < 200 Then
            Dim BlockRegistryKey As RegistryKey = Registry.CurrentUser.OpenSubKey("Software\DSCM\BlockedNodes", True)
            Dim str2 As String = Conversions.ToString(Convert.ToInt64(idString, 16))
            Dim xmlDocument As XmlDocument = New XmlDocument()
            xmlDocument.Load("http://steamcommunity.com/profiles/" + str2 + "?xml=1")
            Dim innerText As String = xmlDocument.SelectSingleNode("/profile/steamID").InnerText
            BlockRegistryKey.SetValue(CType(idStringNoted, Object), innerText)
            dgvBlockedNodes.Rows.Add(CType(innerText, Object), CType(idStringNoted, Object))

            If dsProcess IsNot Nothing Then
                dsProcess.DisconnectSteamId(idString) 'be polite and nicely request a disconnection
                dsProcess.Sync_MemoryBlockList(dgvBlockedNodes.Rows) 'before completely dropping all communications
            End If
        Else
            MsgBox("You can only simulatiously block 200 players. Please remove someone from your block list.", MsgBoxStyle.Critical)
        End If
    End Sub

    Private Function isUserBlocked(idString As String) As Boolean
        idString = idString.ToLower()
        For Each blockNode As DataGridViewRow In dgvBlockedNodes.Rows
            Dim steamID As String = blockNode.Cells("steamId").Value
            steamID = steamID.ToLower()
            If steamID.Contains("_") Then
                steamID = steamID.Remove(steamID.LastIndexOf("_"), 2)
            End If

            If steamID.Equals(idString) Then
                Return True
            End If
        Next
        Return False
    End Function

    Public Async Sub blockUserForMinAccountAge(idStringHex As String)
        Dim idString = Convert.ToInt64(idStringHex, 16).ToString
        'if we have this option turned on
        If mandateMinAccountAge.Checked Then
            'check the account creation date and compare to the specified time ago
            Dim accountCreated = Await lookupUserAccountCreation(idString)
            If accountCreated.HasValue Then
                If accountCreated.Value > Date.Now.AddMonths(-Config.AccountCreatedMinMonthsOld) Then
                    blockUser(idStringHex, BlockTypes.AgeBlock)
                End If
            End If
        End If
    End Sub

    'Lookup the given user, and return their account creation date
    'Return Datetime on success, or Nothing on error
    Private Async Function lookupUserAccountCreation(idString As String) As Task(Of Date?)
        Try
            Dim client As New Net.WebClient()
            Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
            'use async to grab the website
            Dim content As String = Await client.DownloadStringTaskAsync("http://steamcommunity.com/profiles/" + idString + "?xml=1") 'built in timeout here will throw an exception if the site is down

            'load the website content into the xml parser
            Dim xmlElem = XDocument.Parse(content)
            Dim creationDate As String = xmlElem.Elements("profile").Elements("memberSince").First
            Dim dateresult As Date = Date.ParseExact(creationDate, "MMMM dd, yyyy", System.Globalization.DateTimeFormatInfo.InvariantInfo)
            Return dateresult
        Catch ex As Exception
            'generalized failure, return Nothing
        End Try

        Return Nothing
    End Function

    'Lookup the given user, and return their name history
    'Return a list with the ordered history of the avilable past names, including their current one
    'Also get their steam RealName and Custom URL
    'On error the list will be empty
    Private Async Function lookupUserNameHistory(idString As String) As Task(Of List(Of String))
        Try
            Dim names = New List(Of String)
            Dim client As New Net.WebClient()
            Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
            'use async to grab the website
            Dim content As String = Await client.DownloadStringTaskAsync("http://steamcommunity.com/profiles/" + idString + "?xml=1") 'built in timeout here will throw an exception if the site is down

            'load the website content into the xml parser
            Dim xmlElem = XDocument.Parse(content)
            Dim curName As String = xmlElem.Elements("profile").Elements("steamID").First
            names.Add(curName)
            Dim realname As String = xmlElem.Elements("profile").Elements("realname").First
            If realname <> "" Then
                names.Add(realname)
            End If
            Dim customURL As String = xmlElem.Elements("profile").Elements("customURL").First
            If customURL <> "" Then
                names.Add(customURL)
            End If

            'now grab their older names
            Dim contentOlder As String = Await client.DownloadStringTaskAsync("http://steamcommunity.com/profiles/" + idString + "/namehistory")

            'use regex to parse html because fuck visual basic
            Dim result = RegularExpressions.Regex.Matches(contentOlder, "<div class=""historyItem[b]*"">.*<span class=""historyDash"">")
            For Each m As Match In result
                names.Add(m.Groups(1).Value)
            Next

            Return names
        Catch ex As Exception
            'generalized failure, return Nothing
        End Try

        Return New List(Of String)
    End Function

    Private Sub blockUserId_MouseClick(sender As Object, e As EventArgs) Handles blockUserId.Click
        Dim idString As String = verifySteamId(txtBlockSteamID)
        If idString.Equals("") Then
            Return
        End If
        blockUser(idString, BlockTypes.ManualBlock)
    End Sub

    Private Function getSelectedNode() As Tuple(Of String, String)
        Dim currentGrid As DataGridView = Nothing
        If tabs.SelectedTab Is tabActive Then
            currentGrid = dgvMPNodes
        ElseIf tabs.SelectedTab Is tabRecent Then
            currentGrid = dgvRecentNodes
        ElseIf tabs.SelectedTab Is tabFavorites Then
            currentGrid = dgvFavoriteNodes
        ElseIf tabs.SelectedTab Is tabBlock Then
            currentGrid = dgvBlockedNodes
        ElseIf tabs.SelectedTab Is tabDamageLog Then
            currentGrid = dgvDamageLog
        ElseIf tabs.SelectedTab Is tabDSCMNet Then
            currentGrid = dgvDSCMNet
        Else
            Return Nothing
        End If

        If currentGrid.CurrentRow IsNot Nothing Then
            Dim name As String = currentGrid.CurrentRow.Cells("name").Value
            Dim steamId As String = currentGrid.CurrentRow.Cells("steamId").Value
            Return Tuple.Create(steamId, name)
        End If

        Return Nothing
    End Function
    Private Sub dgvNodes_doubleclick(sender As DataGridView, e As EventArgs) Handles dgvFavoriteNodes.DoubleClick,
        dgvRecentNodes.DoubleClick, dgvDSCMNet.DoubleClick
        Dim steamId = sender.CurrentRow.Cells("steamId").Value
        manualConnections.Add(steamId)
        connectToSteamId(steamId)
    End Sub
    Private Sub btnAddFavorite_Click(sender As Object, e As EventArgs) Handles btnAddFavorite.Click
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\FavoriteNodes", True)

        Dim selectedNode = getSelectedNode()
        If selectedNode Is Nothing Then
            MsgBox("No selection detected.")
            Return
        End If

        If key.GetValue(selectedNode.Item1) Is Nothing Then
            key.SetValue(selectedNode.Item1, selectedNode.Item2)
            dgvFavoriteNodes.Rows.Add(selectedNode.Item2, selectedNode.Item1)
        End If
    End Sub
    Private Sub btnRemFavorite_Click(sender As Object, e As EventArgs) Handles btnRemFavorite.Click
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\FavoriteNodes", True)

        Dim selectedNode = getSelectedNode()
        If selectedNode Is Nothing Then
            MsgBox("No selection detected.")
            Return
        End If

        Dim steamId As String = selectedNode.Item1

        If Not key.GetValue(steamId) Is Nothing Then
            key.DeleteValue(steamId)
        End If

        For i = dgvFavoriteNodes.Rows.Count - 1 To 0 Step -1
            If dgvFavoriteNodes.Rows(i).Cells("steamId").Value = steamId Then
                dgvFavoriteNodes.Rows.Remove(dgvFavoriteNodes.Rows(i))
            End If
        Next
    End Sub

    'Button to block selected user
    Private Sub btnAddBlock_Click(sender As Object, e As EventArgs) Handles btnAddBlock.Click
        Dim selectedNode = getSelectedNode()
        If selectedNode Is Nothing Then
            MsgBox("No selection detected.")
            Return
        End If

        Dim steamId As String = selectedNode.Item1

        blockUser(steamId, BlockTypes.ManualBlock)
    End Sub

    'Button to unblock selected user
    Private Sub btnRemBlock_Click(sender As Object, e As EventArgs) Handles btnRemBlock.Click
        Dim key As Microsoft.Win32.RegistryKey
        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\BlockedNodes", True)

        Dim selectedNode = getSelectedNode()
        If selectedNode Is Nothing Then
            MsgBox("No selection detected.")
            Return
        End If

        Dim steamId As String = selectedNode.Item1

        If Not key.GetValue(steamId) Is Nothing Then
            key.DeleteValue(steamId)
        End If

        For i = dgvBlockedNodes.Rows.Count - 1 To 0 Step -1
            If dgvBlockedNodes.Rows(i).Cells("steamId").Value = steamId Then
                dgvBlockedNodes.Rows.Remove(dgvBlockedNodes.Rows(i))
                If dsProcess IsNot Nothing Then
                    dsProcess.Sync_MemoryBlockList(dgvBlockedNodes.Rows)
                End If
            End If
        Next
    End Sub

    Private Sub chkDSCMNet_CheckedChanged(sender As Object, e As EventArgs) Handles chkDSCMNet.CheckedChanged
        Dim key As Microsoft.Win32.RegistryKey

        key = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\Options", True)
        key.SetValue("JoinDSCM-Net", chkDSCMNet.Checked)

        If chkDSCMNet.Checked Then
            _netClient = New NetClient(Me)
            netNodeConnectTimer.Start()
            updateNetNodesTimer.Start()
            publishNodesTimer.Start()
            checkWatchNodeTimer.Start()
            updateNetNodes()
        Else
            If _netClient IsNot Nothing Then
                updateNetNodesTimer.Stop()
                netNodeConnectTimer.Stop()
                publishNodesTimer.Stop()
                checkWatchNodeTimer.Stop()
                _netClient = Nothing
            End If
        End If
    End Sub

    Private Sub mandateMinAccountAge_CheckedChanged(sender As Object, e As EventArgs) Handles mandateMinAccountAge.CheckedChanged
        Dim keyopts = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\Options", True)
        keyopts.SetValue("MinAccountAge", mandateMinAccountAge.Checked)

        'when user turns off min account age, unblock all users blocked by it
        If Not mandateMinAccountAge.Checked Then
            Dim keyblocks = My.Computer.Registry.CurrentUser.OpenSubKey("Software\DSCM\BlockedNodes", True)
            If keyblocks IsNot Nothing Then
                For Each id As String In keyblocks.GetValueNames()
                    If id.Contains(BlockTypes.AgeBlock) Then
                        keyblocks.DeleteValue(id)
                    End If
                Next
            End If

            For i = dgvBlockedNodes.Rows.Count - 1 To 0 Step -1
                If dgvBlockedNodes.Rows(i).Cells("steamId").Value.Contains(BlockTypes.AgeBlock) Then
                    dgvBlockedNodes.Rows.Remove(dgvBlockedNodes.Rows(i))
                    If dsProcess IsNot Nothing Then
                        dsProcess.Sync_MemoryBlockList(dgvBlockedNodes.Rows)
                    End If
                End If
            Next
        End If
    End Sub

    Private Sub dgvNodes_doubleclick(sender As Object, e As EventArgs) Handles dgvRecentNodes.DoubleClick, dgvFavoriteNodes.DoubleClick, dgvDSCMNet.DoubleClick

    End Sub

    Private Sub btnLaunchDS_Click(sender As Object, e As EventArgs) Handles btnLaunchDS.Click
        Try
            Dim proc As New System.Diagnostics.Process()
            proc = Process.Start("steam://rungameid/211420", "")
        Catch ex As Exception
            MsgBox("Error launching." & Environment.NewLine & ex.Message)
        End Try
    End Sub
    'New subroutines for whitelist tab
    Private Sub ActiveAndFavorite_MouseUp(sender As DataGridView, e As MouseEventArgs) Handles dgvMPNodes.MouseUp, dgvFavoriteNodes.MouseUp, dgvRecentNodes.MouseUp, dgvDSCMNet.MouseUp
        If e.Button <> Windows.Forms.MouseButtons.Right Then Return
        Dim info = sender.HitTest(e.X, e.Y)
        If sender.Rows.Count < 1 Then Return
        If info.Type <> DataGridViewHitTestType.Cell Then Return
        sender.CurrentCell = sender.Item(info.ColumnIndex, info.RowIndex)

        Dim cms = New ContextMenuStrip
        Dim item1 = cms.Items.Add("Add Whitelist")
        item1.Tag = 1
        AddHandler item1.Click, AddressOf addWhitelist
        cms.Items.Add(New ToolStripSeparator())
        Dim item2 = cms.Items.Add("Copy Steam ID")
        item1.Tag = 2
        AddHandler item2.Click, AddressOf copySteamID
        cms.Show(sender, e.Location)
    End Sub

    Private Sub addWhitelist(ByVal sender As Object, ByVal e As EventArgs)
        If Not My.Computer.FileSystem.FileExists(WhitelistLocation) Then
            System.IO.File.Create(WhitelistLocation).Dispose()
        End If
        If tabs.SelectedTab Is tabActive Then
            Dim id = dgvMPNodes.SelectedRows(0).Cells("steamId").Value
            AddNewWhitelistEntry(id)
        ElseIf tabs.SelectedTab Is tabFavorites Then
            Dim id = dgvFavoriteNodes.SelectedRows(0).Cells("steamId").Value
            AddNewWhitelistEntry(id)
        ElseIf tabs.SelectedTab Is tabRecent Then
            Dim id = dgvRecentNodes.SelectedRows(0).Cells("steamId").Value
            AddNewWhitelistEntry(id)
        ElseIf tabs.SelectedTab Is tabDSCMNet Then
            Dim id = dgvDSCMNet.SelectedRows(0).Cells("steamId").Value
            AddNewWhitelistEntry(id)
        End If
        loadWhitelistNodes()
        whitelist_CheckedChanged(Nothing, Nothing)
        updateOnlineState()
    End Sub

    Private Sub copySteamID(ByVal sender As Object, ByVal e As EventArgs)
        If Not My.Computer.FileSystem.FileExists(WhitelistLocation) Then
            System.IO.File.Create(WhitelistLocation).Dispose()
        End If
        If tabs.SelectedTab Is tabActive Then
            Dim id = dgvMPNodes.SelectedRows(0).Cells("steamId").Value
            My.Computer.Clipboard.SetText(id)
        ElseIf tabs.SelectedTab Is tabFavorites Then
            Dim id = dgvFavoriteNodes.SelectedRows(0).Cells("steamId").Value
            My.Computer.Clipboard.SetText(id)
        ElseIf tabs.SelectedTab Is tabRecent Then
            Dim id = dgvRecentNodes.SelectedRows(0).Cells("steamId").Value
            My.Computer.Clipboard.SetText(id)
        ElseIf tabs.SelectedTab Is tabDSCMNet Then
            Dim id = dgvDSCMNet.SelectedRows(0).Cells("steamId").Value
            My.Computer.Clipboard.SetText(id)
        ElseIf tabs.SelectedTab Is tabWhitelist Then
            Dim id = dgvWhitelist.SelectedRows(0).Cells("steamId").Value
            My.Computer.Clipboard.SetText(id)
        End If

    End Sub

    Private Sub AddNewWhitelistEntry(id As String)
        Dim whitenodes = My.Computer.FileSystem.ReadAllText(WhitelistLocation).Split(New String() {Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries).ToList()
        If whitenodes.Contains(id) Then Return
        whitenodes.Add(id)
        File.WriteAllLines(WhitelistLocation, whitenodes)
    End Sub

    Private Sub dgvWhitelist_MouseUp(sender As DataGridView, e As MouseEventArgs) Handles dgvWhitelist.MouseUp
        If e.Button <> Windows.Forms.MouseButtons.Right Then Return
        Dim info = sender.HitTest(e.X, e.Y)
        If sender.Rows.Count < 1 Then Return
        If info.Type <> DataGridViewHitTestType.Cell Then Return
        sender.CurrentCell = sender.Item(info.ColumnIndex, info.RowIndex)

        Dim cms = New ContextMenuStrip
        Dim item1 = cms.Items.Add("Remove Whitelist")
        item1.Tag = 1
        AddHandler item1.Click, AddressOf removeWhiteList
        cms.Items.Add(New ToolStripSeparator())
        Dim item2 = cms.Items.Add("Copy Steam ID")
        item1.Tag = 2
        AddHandler item2.Click, AddressOf copySteamID
        cms.Show(sender, e.Location)
    End Sub

    Private Sub removeWhiteList(ByVal sender As Object, ByVal e As EventArgs)
        Dim id = dgvWhitelist.SelectedRows(0).Cells("steamId").Value
        Dim whitenodes = My.Computer.FileSystem.ReadAllText(WhitelistLocation).Split(New String() {Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries).ToList()
        whitenodes.Remove(id)
        File.WriteAllLines(WhitelistLocation, whitenodes)
        loadWhitelistNodes()
        whitelist_CheckedChanged(Nothing, Nothing)
        updateOnlineState()
    End Sub

    Private Sub btnAdd_Click(sender As Object, e As EventArgs) Handles btnAdd.Click
        If String.IsNullOrWhiteSpace(txtWhitelistSteamID.Text) Then Return
        Dim id = txtWhitelistSteamID.Text
        AddNewWhitelistEntry(id)
        loadWhitelistNodes()
        whitelist_CheckedChanged(Nothing, Nothing)
        updateOnlineState()
        txtWhitelistSteamID.Clear()
    End Sub

    Private Sub btnClear_Click(sender As Object, e As EventArgs) Handles btnClear.Click
        If dgvWhitelist.Rows.Count < 1 Then Return
        whitelist.Checked = False
        File.Delete(WhitelistLocation)
        loadWhitelistNodes()
        whitelist_CheckedChanged(Nothing, Nothing)
    End Sub
End Class



Class DebugLogForm
Inherits ListBox
    Private mw As MainWindow
    Private LobbyRegex As New Regex("^SteamMatchmaking|^Property added:|^LobbyData")
    Public Sub Load(mainWindow As MainWindow)
        mw = mainWindow
        AddHandler mw.chkLogDBG.CheckedChanged, AddressOf FilterEntries
        AddHandler mw.chkLogLobby.CheckedChanged, AddressOf FilterEntries
    End Sub
    Private ReadOnly Property ShowDbgEntries As Boolean
        Get
            Return mw.chkLogDBG.Checked
        End Get
    End Property
    Private ReadOnly Property ShowLobbyEntries As Boolean
        Get
            Return mw.chkLogLobby.Checked
        End Get
    End Property
    Private Function EntryAllowed(entry As DebugLogEntry) As Boolean
        If Not ShowDbgEntries AndAlso entry.severity = "DBG" Then Return False
        If Not ShowLobbyEntries AndAlso entry.severity = "DBG" AndAlso LobbyRegex.IsMatch(entry.msg) Then Return False
        Return True
    End Function
    Public Sub FilterEntries()
        Me.BeginUpdate()
        For i = Items.Count - 1 To 0 Step -1
            If Not EntryAllowed(Items(i)) Then
                Items.RemoveAt(i)
            End If
        Next
        Me.EndUpdate()
    End Sub
    Public Sub UpdateFromDS(dsDebugLog As List(Of DebugLogEntry))
        SyncLock dsDebugLog
            If dsDebugLog.Count
                Me.BeginUpdate()
                Dim itemsVisible As Integer = (Me.Height \ Me.ItemHeight)
                Dim scrollToBottom = Me.TopIndex > Items.Count - itemsVisible - 1

                For Each entry In dsDebugLog
                    entry.msg = LogTranslations.TranslateMessage(entry.msg)
                    If EntryAllowed(entry) Then Items.Add(entry)
                Next
                dsDebugLog.Clear()

                While Items.Count > Config.DebugLogLength
                    Items.RemoveAt(0)
                End While

                Me.EndUpdate()
                If scrollToBottom Then Me.TopIndex = Me.Items.Count - 1
            End If
        End SyncLock
    End Sub
    Public Sub InitContextMenu() Handles Me.ContextMenuStripChanged
        If IsNothing(ContextMenuStrip) Then Return
        AddHandler ContextMenuStrip.ItemClicked, AddressOf ContextMenuItemClicked
    End Sub
    Private Sub ContextMenuItemClicked(sender As Object, e As ToolStripItemClickedEventArgs)
        If e.ClickedItem.Name = "copy" Then
            Dim text = String.Join(vbCrLf, SelectedItems.Cast(Of DebugLogEntry).Select(Function(x) x.ToString()).ToArray())
            Clipboard.SetData(DataFormats.UnicodeText, text)
        ElseIf e.ClickedItem.Name = "selectAll" Then
            BeginUpdate()
            For i = 0 To Items.Count - 1
                Me.SetSelected(i, True)
            Next
            EndUpdate()
        End If
    End Sub
End Class

Public Class DebugLogEntry
    Public severity As String
    Public msg As String
    Public ts As Date

    Sub New(ts, severity, msg)
        Me.ts = ts
        Me.severity = severity
        Me.msg = msg
    End Sub

    Public Overrides Function ToString() As String
        Return ts.ToString("hh:mm:ss.fff") & " " & severity & ": " & msg
    End Function
End Class

Class ConnectedNode
    Public node As DSNode
    Public lastGoodTime As Date

    Sub New(node As DSNode)
        Me.node = node
        Me.lastGoodTime = DateTime.UtcNow
    End Sub
End Class