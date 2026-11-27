<?xml version="1.0"?>
<!-- Compile controller-free visual trees from the production layouts. -->
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
 xmlns:w="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <xsl:template match="w:Window"><Grid><xsl:choose>
  <xsl:when test="@Title='RimWise'">
   <Grid.Resources><xsl:apply-templates select="w:Window.Resources/*"/></Grid.Resources>
   <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
   <StackPanel><xsl:apply-templates select=".//*[@x:Name='MainMenu']"/><xsl:apply-templates select=".//*[@x:Name='MainNavigationBackground']"/></StackPanel>
   <Grid Grid.Row="1"><xsl:apply-templates select=".//*[@x:Name='ModEditorBody']"/></Grid>
   <xsl:apply-templates select=".//*[@x:Name='ModEditorFooter']"/>
  </xsl:when>
  <xsl:when test="@Title='自动排序'"><xsl:apply-templates select=".//*[@x:Name='SortingWorkspace']/w:Grid"/></xsl:when>
  <xsl:otherwise><xsl:apply-templates select="w:Border"/></xsl:otherwise>
 </xsl:choose></Grid></xsl:template>
 <xsl:template match="@*|node()"><xsl:copy><xsl:apply-templates select="@*|node()"/></xsl:copy></xsl:template>
 <xsl:template match="w:EventSetter|w:Popup|w:Grid.InputBindings|@Command|@NavigateUri|@IsDefault|@IsCancel|@AllowDrop|@Click|@Checked|@Unchecked|@Closed|@SelectionChanged|@TextChanged|@PasswordChanged|@RequestNavigate|@MouseDoubleClick|@MouseLeftButtonDown|@ValueChanged|@Expanded|@Collapsed|@DragDelta|@DragCompleted|@KeyDown|@KeyUp|@GroupName"/>
</xsl:stylesheet>
